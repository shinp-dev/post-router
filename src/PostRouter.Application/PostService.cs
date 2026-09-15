using PostRouter.Domain;

namespace PostRouter.Application;

public sealed class PostService(IPostRouterStore store, IMaintenanceGate maintenanceGate, IProviderRegistry providers, TimeProvider timeProvider)
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => store.InitializeAsync(cancellationToken);

    public async Task<EnqueueResult> EnqueueAsync(CanonicalPostIntent intent, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        Validate(intent);
        var canonical = CanonicalIntent.Serialize(intent);
        var hash = CanonicalIntent.Hash(canonical);
        return await store.EnqueueAsync(intent, canonical, hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostSummary?> GetAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetPostAsync(postId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueueItem>> QueueAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetQueueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Account>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CancelAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await store.RequestCancelAsync(postId, cancellationToken).ConfigureAwait(false);
    }

    private void Validate(CanonicalPostIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.ClientRequestId)) throw new ArgumentException("clientRequestId is required.");
        if (intent.Targets.Count == 0) throw new ArgumentException("At least one target is required.");
        if (intent.Targets.Select(x => x.AccountId).Distinct().Count() != intent.Targets.Count)
            throw new ArgumentException("A target account may appear only once.");
        foreach (var target in intent.Targets) _ = providers.GetRequired(target.ProviderKey);
        foreach (var target in intent.Targets)
        {
            using var options = System.Text.Json.JsonDocument.Parse(target.CanonicalOptionsJson);
            if (options.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("Provider options must be a JSON object.");
        }
        if (intent.Schedule.MaxLateness < TimeSpan.Zero) throw new ArgumentException("maxLateness must not be negative.");
        if (intent.Schedule.Mode == ScheduleMode.AtTime && intent.Schedule.DueAtUtc + intent.Schedule.MaxLateness < timeProvider.GetUtcNow())
            throw new ArgumentException("The schedule is already outside maxLateness.");
        if (intent.Content.Kind == ContentKind.TextOnly && string.IsNullOrEmpty(intent.Content.Text))
            throw new ArgumentException("Text-only content requires text.");
        if (intent.Content.Kind == ContentKind.Video && intent.Content.MediaAssets.Count != 1)
            throw new ArgumentException("Video content requires exactly one asset.");
        if (intent.Content.Kind == ContentKind.ImageSet && intent.Content.MediaAssets.Count == 0)
            throw new ArgumentException("Image content requires at least one asset.");
    }
}
