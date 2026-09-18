using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

// Each operation gets its own copy, so cleanup cannot remove a publication's shared spool file.
public sealed class TemporaryPublicMediaPayloadStore(string dataDirectory) : ITemporaryPublicMediaPayloadStore
{
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "media-staging-payload");

    public async Task<MediaAsset> ImportAsync(Guid operationId, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var directory = OperationDirectory(operationId);
        Directory.CreateDirectory(_root);
        if (Directory.Exists(directory)) throw new IOException("Media staging payload already exists.");
        Directory.CreateDirectory(directory);
        try
        {
            return await new SpoolStore(directory, 2L * 1024 * 1024 * 1024)
                .ImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await DeleteAsync(operationId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Preserve the import failure; the dedicated directory is isolated.
            }
            throw;
        }
    }

    public Task DeleteAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var directory = OperationDirectory(operationId);
        if (!Directory.Exists(directory)) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Media staging payload directory is invalid.");
        if (Directory.EnumerateDirectories(directory).Any())
            throw new IOException("Media staging payload directory is invalid.");
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        Directory.Delete(directory);
        return Task.CompletedTask;
    }

    private string OperationDirectory(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Media operation ID is invalid.");
        return Path.Combine(_root, operationId.ToString("N"));
    }
}
