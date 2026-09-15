namespace PostRouter.Application;

public sealed class StatsService(IPostRouterStore store, IMaintenanceGate maintenanceGate, IProviderRegistry providers, TimeProvider timeProvider)
{
    public async Task<Domain.MetricsSnapshot> SyncAsync(string providerKey, Guid accountId, string subjectRef, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        var adapter = providers.GetRequired(providerKey) as IMetricsProvider
            ?? throw new NotSupportedException($"Provider '{providerKey}' does not support metrics.");
        var account = (await store.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(candidate => candidate.Id == accountId);
        if (account is null) throw new KeyNotFoundException("Account not found.");
        if (!string.Equals(account.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Account does not belong to the selected provider.");
        var write = await adapter.FetchMetricsAsync(accountId, subjectRef, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (write.Raw.ExpiresAt <= timeProvider.GetUtcNow()) throw new InvalidDataException("Provider returned already-expired raw metrics.");
        if (write.Raw.Payload.Length > 16 * 1024 * 1024) throw new InvalidDataException("Provider raw metrics exceed the 16 MiB limit.");
        await store.SaveStatsAsync(write, cancellationToken).ConfigureAwait(false);
        return write.Snapshot;
    }

    public async Task SaveAsync(RawMetricInput raw, Domain.MetricsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        if (raw.ExpiresAt <= timeProvider.GetUtcNow()) throw new ArgumentException("Raw metrics are already expired.");
        if (raw.Payload.Length > 16 * 1024 * 1024) throw new ArgumentException("Raw metrics exceed the 16 MiB limit.");
        await store.SaveStatsAsync(new StatsWrite(raw, snapshot), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Domain.MetricsSnapshot>> ShowAsync(Guid accountId, string subjectRef, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetStatsAsync(accountId, subjectRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PurgeExpiredRawAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MaintenanceService(IDatabaseMaintenance database, IPostRouterStore store, TimeProvider timeProvider)
{
    public Task<BackupResult> BackupAsync(string destination, CancellationToken cancellationToken = default) => database.BackupAsync(destination, cancellationToken);

    public async Task RestoreAsync(string backup, CancellationToken cancellationToken = default)
    {
        await database.RestoreAsync(backup, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DoctorCheck>> DoctorAsync(CancellationToken cancellationToken = default)
    {
        var db = await database.CheckAsync(cancellationToken).ConfigureAwait(false);
        var storeChecks = await store.CheckAsync(cancellationToken).ConfigureAwait(false);
        return [.. db, .. storeChecks];
    }

    public Task<RestoreStatus> RestoreStatusAsync(CancellationToken cancellationToken = default) => store.GetRestoreStatusAsync(cancellationToken);
    public Task SuppressAsync(Guid publicationId, string reason, CancellationToken cancellationToken = default) =>
        store.SuppressRestoredPublicationAsync(publicationId, reason, timeProvider.GetUtcNow(), cancellationToken);
    public Task ReleaseAsync(CancellationToken cancellationToken = default) => store.ReleaseQuarantineAsync(timeProvider.GetUtcNow(), cancellationToken);
}
