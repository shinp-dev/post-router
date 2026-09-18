using PostRouter.Domain;

namespace PostRouter.Application;

public sealed record PublicMediaOperationRecord(
    Guid Id, string Sha256, long SizeBytes, string MimeType, PublicMediaStagingOperation Operation,
    StagedPublicAsset? Staged, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? LastErrorCode);

public sealed record PublicMediaOperationView(
    Guid Id, string Status, string MediaType, long SizeBytes, string? PublicUrl,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? LastErrorCode);

public interface IPublicMediaOperationStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PublicMediaOperationRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicMediaOperationRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PublicMediaOperationRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ITemporaryPublicMediaPayloadStore
{
    Task<MediaAsset> ImportAsync(Guid operationId, string sourcePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed class PublicMediaOperations(ITemporaryPublicMediaPayloadStore payloads, IPublicMediaOperationStore journal)
{
    private const string PayloadCleanupFailed = "media_payload_cleanup_failed";

    public async Task<PublicMediaOperationView> StageAsync(string sourcePath, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
        => await StageAsync(Guid.NewGuid(), sourcePath, host, cancellationToken).ConfigureAwait(false);

    // A publication supplies its durable ID. An existing journal entry is only recovered,
    // never uploaded again after a lost response or a worker restart.
    public async Task<PublicMediaOperationView> StageAsync(Guid id, string sourcePath, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        MediaAsset? source = null;
        PublicMediaOperationRecord? record = null;
        var cleanupFailedWithoutRecord = false;
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        record = await journal.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            if (record.Staged is not null) return View(record);
            var recovered = await host.RecoverAsync(record.Operation, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                record = record with { Staged = recovered, LastErrorCode = null, UpdatedAt = DateTimeOffset.UtcNow };
                await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            }
            return View(record);
        }
        try
        {
            source = await payloads.ImportAsync(id, sourcePath, cancellationToken).ConfigureAwait(false);
            var operation = host.Prepare(source);
            var now = DateTimeOffset.UtcNow;
            record = new(id, source.Sha256, source.SizeBytes, source.DetectedMime, operation, null, now, now, null);
            await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            try
            {
                var staged = await host.StageAsync(source, operation, cancellationToken).ConfigureAwait(false);
                record = record with { Staged = staged, UpdatedAt = DateTimeOffset.UtcNow };
            }
            catch (TemporaryPublicMediaException error)
            {
                record = record with { LastErrorCode = error.Code, UpdatedAt = DateTimeOffset.UtcNow };
            }
            await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Pending recovery only queries GitHub. The copied payload is never needed again.
            if (source is not null)
            {
                try { await payloads.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (record is null) cleanupFailedWithoutRecord = true;
                    else
                    {
                        record = record with { LastErrorCode = PayloadCleanupFailed, UpdatedAt = DateTimeOffset.UtcNow };
                        await journal.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        if (cleanupFailedWithoutRecord) throw new TemporaryPublicMediaException(PayloadCleanupFailed);
        return View(record!);
    }

    public async Task<PublicMediaOperationView> RecoverAsync(Guid id, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        var record = await RequiredAsync(id, cancellationToken).ConfigureAwait(false);
        record = await RetryPayloadCleanupAsync(record, cancellationToken).ConfigureAwait(false);
        if (record.Staged is not null) return View(record);
        var cleanupStillFailed = record.LastErrorCode == PayloadCleanupFailed;
        try
        {
            var staged = await host.RecoverAsync(record.Operation, cancellationToken).ConfigureAwait(false);
            record = record with
            {
                Staged = staged,
                LastErrorCode = cleanupStillFailed ? PayloadCleanupFailed :
                    staged is null ? "github_asset_not_found" : null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (TemporaryPublicMediaException error)
        {
            record = record with
            {
                LastErrorCode = cleanupStillFailed ? PayloadCleanupFailed : error.Code,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return View(record);
    }

    public async Task<PublicMediaOperationView> DeleteAsync(Guid id, ITemporaryPublicMediaHost host,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await journal.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        var record = await RequiredAsync(id, cancellationToken).ConfigureAwait(false);
        // Keep the operation ID while a failed local cleanup still needs retrying.
        await payloads.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (record.Staged is null) throw new InvalidOperationException("media_not_staged");
        await host.DeleteAsync(record.Staged.Handle, cancellationToken).ConfigureAwait(false);
        await journal.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return View(record) with { Status = "Deleted", PublicUrl = null, LastErrorCode = null };
    }

    public async Task<PublicMediaOperationView> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        View(await RequiredAsync(id, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<PublicMediaOperationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await journal.ListAsync(cancellationToken).ConfigureAwait(false);
        return records.OrderByDescending(record => record.CreatedAt).Select(View).ToArray();
    }

    private async Task<PublicMediaOperationRecord> RetryPayloadCleanupAsync(PublicMediaOperationRecord record,
        CancellationToken cancellationToken)
    {
        if (record.LastErrorCode != PayloadCleanupFailed) return record;
        try { await payloads.DeleteAsync(record.Id, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return record; }
        record = record with { LastErrorCode = null, UpdatedAt = DateTimeOffset.UtcNow };
        await journal.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private async Task<PublicMediaOperationRecord> RequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await journal.ReadAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("Media operation was not found.");

    private static PublicMediaOperationView View(PublicMediaOperationRecord record) =>
        new(record.Id, record.Staged is null ? "Pending" : "Staged", record.MimeType, record.SizeBytes,
            record.Staged?.PublicUrl.AbsoluteUri, record.CreatedAt, record.Staged?.ExpiresAt, record.LastErrorCode);
}
