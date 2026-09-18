using System.Text.Json;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class FileGitHubMediaConfigurationStore(string dataDirectory) : IGitHubMediaConfigurationStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "github-media-settings.json");
    private readonly string _lockPath = Path.Combine(Path.GetFullPath(dataDirectory), "github-media-settings.lock");

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        await FileLockHelpers.TryOpenAsync(_lockPath, FileAccess.ReadWrite, FileShare.None,
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)
        ?? throw new TimeoutException("GitHub media settings are busy.");

    public async Task<GitHubMediaConfiguration?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 4096) throw new InvalidDataException("GitHub media settings are invalid.");
        var value = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (value is not { Version: 1, Configuration: not null })
            throw new InvalidDataException("GitHub media settings are invalid.");
        return value.Configuration;
    }

    public async Task SaveAsync(GitHubMediaConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new SettingsDocument(1, configuration),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SettingsDocument(int Version, GitHubMediaConfiguration? Configuration);
}
