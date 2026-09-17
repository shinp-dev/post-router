using System.Text.Json;
using PostRouter.Domain;

namespace PostRouter.Application;

public sealed class OperationsService(
    IPostRouterStore store,
    IMaintenanceGate maintenanceGate,
    IProviderRegistry providers,
    PostService posts,
    TimeProvider timeProvider,
    IPublicationApprovalStore? approvalStore = null)
{
    private readonly IPublicationApprovalStore _approvalStore = approvalStore ?? PassThroughPublicationApprovalStore.Instance;

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

    public async Task<PublicationApprovalStatus> PublicationApprovalAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        return await _approvalStore.GetStatusAsync(publicationId, cancellationToken).ConfigureAwait(false);
    }

    public Task<EnqueueResult> EnqueueTextAsync(CreateTextPostRequest request, CancellationToken cancellationToken = default) =>
        EnqueueTextAsync(request, ApprovalPolicy.Automatic, cancellationToken);

    public async Task<EnqueueResult> EnqueueTextAsync(CreateTextPostRequest request, ApprovalPolicy approvalPolicy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Post text is required.");
        return await EnqueueAsync(request.AccountId, request.Text, [], ContentKind.TextOnly,
            request.PublishAt, request.ClientRequestId, approvalPolicy, cancellationToken).ConfigureAwait(false);
    }

    public Task<EnqueueResult> EnqueueImagesAsync(CreateImagePostRequest request, CancellationToken cancellationToken = default) =>
        EnqueueImagesAsync(request, ApprovalPolicy.Automatic, cancellationToken);

    public async Task<EnqueueResult> EnqueueImagesAsync(CreateImagePostRequest request, ApprovalPolicy approvalPolicy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Post text is required.");
        if (request.Images.Count is < 1 or > 4) throw new ArgumentException("Image posts require one to four images.");
        return await EnqueueAsync(request.AccountId, request.Text, request.Images, ContentKind.ImageSet,
            request.PublishAt, request.ClientRequestId, approvalPolicy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnqueueResult> EnqueueVideoAsync(CreateVideoPostRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Video.DetectedMime != "video/mp4") throw new ArgumentException("Video must be MP4.");
        if (request.MadeForKids is null) throw new ArgumentException("Made for Kids must be selected.");
        if (!request.UploadNoticeAcknowledged) throw new ArgumentException("YouTube upload notice must be acknowledged.");
        if (request.Visibility != "private") throw new ArgumentException("GUI video visibility must be private.");
        var accounts = await posts.AccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.SingleOrDefault(candidate => candidate.Id == request.AccountId)
            ?? throw new KeyNotFoundException("Account not found.");
        var adapter = providers.GetRequired(account.ProviderKey);
        var capability = adapter.Capabilities;
        if (account.ProviderKey != "youtube" || !capability.ContentKinds.Contains(ContentKind.Video) ||
            !capability.Visibilities.Contains("private") || capability.OptionsSchema != "youtube-options/v1" ||
            capability.OptionsVersion != 1)
            throw new NotSupportedException("The selected provider does not support private YouTube video.");
        var target = new TargetIntent(account.Id, account.ProviderKey, "private", capability.OptionsSchema,
            capability.OptionsVersion, JsonSerializer.Serialize(new
            {
                madeForKids = request.MadeForKids.Value,
                containsSyntheticMedia = request.ContainsSyntheticMedia,
                uploadNoticeAcknowledged = true,
            }), account.Alias);
        var intent = new CanonicalPostIntent(
            string.IsNullOrWhiteSpace(request.ClientRequestId) ? $"gui-{Guid.NewGuid():N}" : request.ClientRequestId,
            new Content(Guid.NewGuid(), ContentKind.Video, request.Description, request.Title, [request.Video]),
            [target], new ScheduleIntent(ScheduleMode.Immediate, timeProvider.GetUtcNow(), TimeSpan.FromMinutes(15)));
        return await posts.EnqueueAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnqueueResult> EnqueueAsync(Guid accountId, string text, IReadOnlyList<MediaAsset> media,
        ContentKind kind, DateTimeOffset? publishAt, string? clientRequestId, ApprovalPolicy approvalPolicy, CancellationToken cancellationToken)
    {
        var accounts = await posts.AccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.SingleOrDefault(candidate => candidate.Id == accountId)
            ?? throw new KeyNotFoundException("Account not found.");
        var adapter = providers.GetRequired(account.ProviderKey);
        var capability = adapter.Capabilities;
        if (!capability.ContentKinds.Contains(kind))
            throw new NotSupportedException("The selected provider does not support this content kind.");
        if (capability.Visibilities.Count == 0)
            throw new NotSupportedException("The selected provider has no supported visibility.");
        var visibility = capability.Visibilities[0];
        var now = timeProvider.GetUtcNow();
        var dueAt = publishAt?.ToUniversalTime() ?? now;
        var schedule = new ScheduleIntent(
            publishAt is null ? ScheduleMode.Immediate : ScheduleMode.AtTime,
            dueAt,
            TimeSpan.FromMinutes(15),
            publishAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            null,
            publishAt?.Offset);
        var intent = new CanonicalPostIntent(
            string.IsNullOrWhiteSpace(clientRequestId) ? $"gui-{Guid.NewGuid():N}" : clientRequestId,
            new Content(Guid.NewGuid(), kind, text, null, media),
            [new TargetIntent(account.Id, account.ProviderKey, visibility, capability.OptionsSchema,
                capability.OptionsVersion, capability.DefaultOptionsJson, account.Alias, approvalPolicy)],
            schedule);
        return await posts.EnqueueAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CancelAsync(Guid postId, CancellationToken cancellationToken = default) =>
        posts.CancelAsync(postId, cancellationToken);

    public async Task ApproveAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        if (!await _approvalStore.ApproveAsync(publicationId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Publication does not require approval or cannot be approved in its current state.");
    }

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
