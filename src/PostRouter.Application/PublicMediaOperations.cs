using PostRouter.Domain;

namespace PostRouter.Application;

public sealed record PublicMediaOperationRecord(
    Guid Id, MediaAsset Source, PublicMediaStagingOperation Operation,
    StagedPublicAsset? Staged, bool Deleted, DateTimeOffset CreatedAt, string? LastErrorCode);

public sealed record PublicMediaOperationView(
    Guid Id, string Status, string MediaType, long SizeBytes, string? PublicUrl,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? LastErrorCode);

public interface IPublicMediaOperationStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PublicMediaOperationRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicMediaOperationRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PublicMediaOperationRecord record, CancellationToken cancellationToken = default);
}

public sealed class PublicMediaOperations(ISpoolStore spool, IPublicMediaOperationStore journal)
{
    public async Task<PublicMediaOperationView> StageAsync(string sourcePath, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        var source = await spool.ImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var operation = host.Prepare(source);
        var id = Guid.NewGuid();
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        var record = new PublicMediaOperationRecord(id, source, operation, null, false, DateTimeOffset.UtcNow, null);
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        try
        {
            var staged = await host.StageAsync(source, operation, cancellationToken).ConfigureAwait(false);
            record = record with { Staged = staged };
        }
        catch (TemporaryPublicMediaException error)
        {
            record = record with { LastErrorCode = error.Code };
        }
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return View(record);
    }

    public async Task<PublicMediaOperationView> RecoverAsync(Guid id, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        var record = await RequiredAsync(id, cancellationToken).ConfigureAwait(false);
        if (record.Deleted || record.Staged is not null) return View(record);
        try
        {
            var staged = await host.RecoverAsync(record.Operation, cancellationToken).ConfigureAwait(false);
            record = record with { Staged = staged, LastErrorCode = staged is null ? "github_asset_not_found" : null };
        }
        catch (TemporaryPublicMediaException error)
        {
            record = record with { LastErrorCode = error.Code };
        }
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return View(record);
    }

    public async Task<PublicMediaOperationView> DeleteAsync(Guid id, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        var record = await RequiredAsync(id, cancellationToken).ConfigureAwait(false);
        if (record.Deleted) return View(record);
        if (record.Staged is null) throw new InvalidOperationException("media_not_staged");
        await host.DeleteAsync(record.Staged.Handle, cancellationToken).ConfigureAwait(false);
        record = record with { Deleted = true, LastErrorCode = null };
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return View(record);
    }

    public async Task<PublicMediaOperationView> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        View(await RequiredAsync(id, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<PublicMediaOperationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await journal.ListAsync(cancellationToken).ConfigureAwait(false);
        return records.Where(record => !record.Deleted).OrderByDescending(record => record.CreatedAt)
            .Concat(records.Where(record => record.Deleted).OrderByDescending(record => record.CreatedAt).Take(100))
            .Select(View).ToArray();
    }

    private async Task<PublicMediaOperationRecord> RequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await journal.ReadAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("Media operation was not found.");

    private static PublicMediaOperationView View(PublicMediaOperationRecord record) =>
        new(record.Id, record.Deleted ? "Deleted" : record.Staged is null ? "Pending" : "Staged",
            record.Source.DetectedMime, record.Source.SizeBytes,
            record.Deleted ? null : record.Staged?.PublicUrl.AbsoluteUri,
            record.CreatedAt, record.Staged?.ExpiresAt, record.LastErrorCode);
}
