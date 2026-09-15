using PostRouter.Domain;

namespace PostRouter.Application;

public sealed class OperationsService(
    IPostRouterStore store,
    IMaintenanceGate maintenanceGate,
    IProviderRegistry providers,
    PostService posts,
    TimeProvider timeProvider)
{
    public IReadOnlyList<ProviderCapabilities> Capabilities() => providers.GetCapabilities();

    public async Task<DashboardSummary> DashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetDashboardAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountConnection>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAccountConnectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicationListItem>> PublicationsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetPublicationsAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicationDetail?> PublicationAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetPublicationDetailAsync(publicationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnqueueResult> EnqueueTextAsync(CreateTextPostRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Post text is required.");
        var accounts = await posts.AccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.SingleOrDefault(candidate => candidate.Id == request.AccountId)
            ?? throw new KeyNotFoundException("Account not found.");
        var adapter = providers.GetRequired(account.ProviderKey);
        var capability = adapter.Capabilities;
        if (!capability.ContentKinds.Contains(ContentKind.TextOnly))
            throw new NotSupportedException("The selected provider does not support text posts.");
        var visibility = capability.Visibilities.FirstOrDefault()
            ?? throw new NotSupportedException("The selected provider has no supported visibility.");
        var now = timeProvider.GetUtcNow();
        var dueAt = request.PublishAt?.ToUniversalTime() ?? now;
        var schedule = new ScheduleIntent(
            request.PublishAt is null ? ScheduleMode.Immediate : ScheduleMode.AtTime,
            dueAt,
            TimeSpan.FromMinutes(15),
            request.PublishAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            null,
            request.PublishAt?.Offset);
        var intent = new CanonicalPostIntent(
            string.IsNullOrWhiteSpace(request.ClientRequestId) ? $"gui-{Guid.NewGuid():N}" : request.ClientRequestId,
            new Content(Guid.NewGuid(), ContentKind.TextOnly, request.Text, null, []),
            [new TargetIntent(account.Id, account.ProviderKey, visibility, capability.OptionsSchema,
                capability.OptionsVersion, capability.DefaultOptionsJson, account.Alias)],
            schedule);
        return await posts.EnqueueAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CancelAsync(Guid postId, CancellationToken cancellationToken = default) =>
        posts.CancelAsync(postId, cancellationToken);

    public async Task RetryAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        if (!await store.RequestRetryAsync(publicationId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Publication is not safely retryable.");
    }

    public async Task ReconcileAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        if (!await store.RequestReconcileAsync(publicationId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Publication cannot be reconciled in its current state.");
    }
}
