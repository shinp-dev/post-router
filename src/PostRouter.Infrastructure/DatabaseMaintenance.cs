using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class DatabaseMaintenance(SqliteDatabase database, IMaintenanceGate gate, string spoolDirectory) : IDatabaseMaintenance
{
    private readonly string _spoolDirectory = Path.GetFullPath(spoolDirectory);

    public async Task<BackupResult> BackupAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        await using var lease = await gate.TryAcquireExclusiveAsync(TimeSpan.FromSeconds(30), cancellationToken) ?? throw new TimeoutException("Maintenance gate is busy.");
        return await CreateBackupSetAsync(destinationDirectory, "post-router", cancellationToken).ConfigureAwait(false);
    }

    private async Task<BackupResult> CreateBackupSetAsync(string destinationDirectory, string prefix, CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        if (IsWithin(destinationRoot, _spoolDirectory)) throw new InvalidOperationException("Backup destination cannot be inside the managed spool directory.");
        Directory.CreateDirectory(destinationRoot);
        var created = DateTimeOffset.UtcNow;
        var path = Path.Combine(destinationRoot, $"{prefix}-{created:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
        await using (var source = await database.OpenAsync(cancellationToken))
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        var hash = await HashFileAsync(path, cancellationToken);
        var backupSpool = path + ".spool";
        Directory.CreateDirectory(backupSpool);
        var spoolFiles = new List<BackupFile>();
        if (Directory.Exists(_spoolDirectory))
        {
            foreach (var sourceFile in Directory.EnumerateFiles(_spoolDirectory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(sourceFile);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Spool reparse points cannot be backed up.");
                var name = Path.GetFileName(sourceFile);
                var destination = Path.Combine(backupSpool, name);
                File.Copy(sourceFile, destination, overwrite: false);
                spoolFiles.Add(new(name, await HashFileAsync(destination, cancellationToken), info.Length));
            }
        }
        var manifest = JsonSerializer.Serialize(new BackupManifest(1, Path.GetFileName(path), hash, created, spoolFiles));
        await File.WriteAllTextAsync(path + ".manifest.json", manifest, cancellationToken);
        return new(path, hash, created);
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await using var lease = await gate.TryAcquireExclusiveAsync(TimeSpan.FromSeconds(30), cancellationToken) ?? throw new TimeoutException("Maintenance gate is busy.");
        var source = Path.GetFullPath(backupPath);
        if (string.Equals(source, database.DatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The active database cannot be used as its own restore source.");
        var manifestPath = source + ".manifest.json";
        if (!File.Exists(source) || !File.Exists(manifestPath)) throw new FileNotFoundException("Backup set is incomplete.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken))
            ?? throw new InvalidDataException("Backup manifest is invalid.");
        if (manifest.SchemaVersion != 1 || !string.Equals(manifest.Database, Path.GetFileName(source), StringComparison.Ordinal))
            throw new InvalidDataException("Backup manifest does not match the selected database.");
        if (!string.Equals(manifest.Sha256, await HashFileAsync(source, cancellationToken), StringComparison.Ordinal)) throw new InvalidDataException("Backup hash mismatch.");
        var backupSpool = source + ".spool";
        if (!Directory.Exists(backupSpool)) throw new FileNotFoundException("Backup spool set is incomplete.");
        foreach (var file in manifest.SpoolFiles)
        {
            if (!string.Equals(file.Name, Path.GetFileName(file.Name), StringComparison.Ordinal)) throw new InvalidDataException("Backup spool path is invalid.");
            var path = Path.Combine(backupSpool, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !string.Equals(file.Sha256, await HashFileAsync(path, cancellationToken), StringComparison.Ordinal))
                throw new InvalidDataException($"Backup spool file '{file.Name}' failed validation.");
        }
        await using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await check.OpenAsync(cancellationToken);
            var integrity = await SqliteDatabase.ScalarAsync<string>(check, null, "PRAGMA integrity_check", cancellationToken);
            if (integrity != "ok") throw new InvalidDataException("Backup database failed integrity_check.");
        }
        var safetyBackup = await CreateBackupSetAsync(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "backups"), "pre-restore", cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        var temp = database.DatabasePath + ".restore.tmp";
        File.Copy(source, temp, true);
        var stagedSpool = _spoolDirectory + ".restore.tmp";
        if (Directory.Exists(stagedSpool)) Directory.Delete(stagedSpool, recursive: true);
        Directory.CreateDirectory(stagedSpool);
        foreach (var file in manifest.SpoolFiles) File.Copy(Path.Combine(backupSpool, file.Name), Path.Combine(stagedSpool, file.Name));
        await using (var staged = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await staged.OpenAsync(cancellationToken);
            await SqliteDatabase.ExecuteAsync(staged, null, "PRAGMA journal_mode=DELETE", cancellationToken);
            await SqliteDatabase.ExecuteAsync(staged, null, "UPDATE installations SET quarantined=1,restore_epoch=restore_epoch+1", cancellationToken);
            await SqliteDatabase.ExecuteAsync(staged, null, "INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at) VALUES($id,'Restore','Installation','default',$reason,$at)", cancellationToken,
                ("$id", Guid.NewGuid().ToString("D")), ("$reason", $"safety_backup:{Path.GetFileName(safetyBackup.Path)}"), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        }
        File.Move(temp, database.DatabasePath, true);
        var previousSpool = _spoolDirectory + ".restore.previous";
        if (Directory.Exists(previousSpool)) Directory.Delete(previousSpool, recursive: true);
        if (Directory.Exists(_spoolDirectory)) Directory.Move(_spoolDirectory, previousSpool);
        Directory.Move(stagedSpool, _spoolDirectory);
        SqliteConnection.ClearAllPools();
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(previousSpool)) Directory.Delete(previousSpool, recursive: true);
    }

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            var value = await SqliteDatabase.ScalarAsync<string>(connection, null, "PRAGMA integrity_check", cancellationToken);
            return [new("integrity", value == "ok", value == "ok" ? "ok" : "corrupt", value ?? "no result")];
        }
        catch (Exception ex) { return [new("integrity", false, "unavailable", ex.GetType().Name)]; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsWithin(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(root, candidate, comparison) || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record BackupFile(string Name, string Sha256, long Size);
    private sealed record BackupManifest(int SchemaVersion, string Database, string Sha256, DateTimeOffset CreatedAt, IReadOnlyList<BackupFile> SpoolFiles);
}
