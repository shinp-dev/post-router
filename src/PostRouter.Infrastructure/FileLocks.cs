using Microsoft.Win32.SafeHandles;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

internal static class FileLockHelpers
{
    public static async ValueTask<FileStream?> TryOpenAsync(string path, FileAccess access, FileShare share, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, access, share, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline) return null;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        } while (DateTimeOffset.UtcNow < deadline);
        return null;
    }
}

public sealed class FileWorkerLockFactory(string lockPath) : IWorkerLockFactory
{
    public async ValueTask<IWorkerLock?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var stream = await FileLockHelpers.TryOpenAsync(Path.GetFullPath(lockPath), FileAccess.ReadWrite, FileShare.None, TimeSpan.Zero, cancellationToken);
        return stream is null ? null : new FileWorkerLock(stream);
    }
    private sealed class FileWorkerLock(FileStream stream) : IWorkerLock { public ValueTask DisposeAsync() => stream.DisposeAsync(); }
}

public sealed class FileMaintenanceGate(string directory) : IMaintenanceGate
{
    private readonly string _writerPath = Path.Combine(Path.GetFullPath(directory), "maintenance.writer.lock");
    private readonly string _resourcePath = Path.Combine(Path.GetFullPath(directory), "maintenance.resource.lock");

    public async ValueTask<IMaintenanceLease> AcquireSharedAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var writer = await FileLockHelpers.TryOpenAsync(_writerPath, FileAccess.ReadWrite, FileShare.None, TimeSpan.FromSeconds(30), cancellationToken)
                ?? throw new TimeoutException("Maintenance gate is busy.");
            try
            {
                var resource = await FileLockHelpers.TryOpenAsync(_resourcePath, FileAccess.Read, FileShare.Read, TimeSpan.FromSeconds(30), cancellationToken);
                if (resource is not null) return new Lease(resource, null);
            }
            finally { await writer.DisposeAsync(); }
        }
    }

    public async ValueTask<IMaintenanceLease?> TryAcquireExclusiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var writer = await FileLockHelpers.TryOpenAsync(_writerPath, FileAccess.ReadWrite, FileShare.None, timeout, cancellationToken);
        if (writer is null) return null;
        var resource = await FileLockHelpers.TryOpenAsync(_resourcePath, FileAccess.ReadWrite, FileShare.None, timeout, cancellationToken);
        if (resource is null) { await writer.DisposeAsync(); return null; }
        return new Lease(resource, writer);
    }

    private sealed class Lease(FileStream resource, FileStream? writer) : IMaintenanceLease
    {
        public async ValueTask DisposeAsync()
        {
            await resource.DisposeAsync();
            if (writer is not null) await writer.DisposeAsync();
        }
    }
}

public sealed class FileAuthGrantLockFactory(string directory) : IAuthGrantLockFactory
{
    public async ValueTask<IAuthGrantLock> AcquireAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var stream = await FileLockHelpers.TryOpenAsync(Path.Combine(Path.GetFullPath(directory), $"grant-{grantId:D}.lock"), FileAccess.ReadWrite, FileShare.None, TimeSpan.FromSeconds(30), cancellationToken)
            ?? throw new TimeoutException("Authentication grant is busy.");
        return new GrantLock(stream);
    }
    private sealed class GrantLock(FileStream stream) : IAuthGrantLock { public ValueTask DisposeAsync() => stream.DisposeAsync(); }
}
