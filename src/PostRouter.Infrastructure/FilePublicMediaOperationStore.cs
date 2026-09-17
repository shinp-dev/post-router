using System.Text.Json;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class FilePublicMediaOperationStore(string dataDirectory) : IPublicMediaOperationStore
{
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "github-media-operations");

    public async ValueTask<IAsyncDisposable> AcquireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Media operation ID is invalid.");
        var stream = await FileLockHelpers.TryOpenAsync(Path.Combine(_root, $"{id:N}.lock"),
            FileAccess.ReadWrite, FileShare.None, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return stream ?? throw new TimeoutException("Media operation is busy.");
    }

    public async Task<PublicMediaOperationRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Media operation ID is invalid.");
        var path = Path.Combine(_root, $"{id:N}.json");
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 16384) throw new InvalidDataException("Media operation record is invalid.");
        var value = await JsonSerializer.DeserializeAsync<PublicMediaOperationRecord>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (value is null || value.Id != id || value.Operation is null || value.Source is null)
            throw new InvalidDataException("Media operation record is invalid.");
        return value;
    }

    public async Task<IReadOnlyList<PublicMediaOperationRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return [];
        var records = new List<PublicMediaOperationRecord>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                records.Add(await ReadAsync(id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Media operation record is missing."));
        }
        return records;
    }

    public async Task SaveAsync(PublicMediaOperationRecord record, CancellationToken cancellationToken = default)
    {
        if (record.Id == Guid.Empty) throw new ArgumentException("Media operation ID is invalid.");
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{record.Id:N}.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
