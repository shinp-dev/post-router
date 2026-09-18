using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

internal sealed record InstagramReelCheckpoint(
    Guid? StageOperationId = null, string? ContainerId = null, DateTimeOffset? ContainerCreatedAt = null,
    bool ContainerFinished = false, DateTimeOffset? FinishedCheckedAt = null, string? MediaId = null);

internal sealed record InstagramReelPlan(string Operation, ProviderPublication Input, InstagramReelCheckpoint Checkpoint);

internal sealed class InstagramReelProviderAdapter(
    AuthCoordinator auth, IPostRouterStore store, ISpoolStore spool, PublicMediaOperations media,
    Func<CancellationToken, Task<ITemporaryPublicMediaHost>> createHost,
    InstagramPublishingClient client, TimeProvider time) : IProviderAdapter
{
    private const long MaximumVideoBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FinishedSnapshotLifetime = TimeSpan.FromMinutes(1);

    public string ProviderKey => "instagram";
    public ProviderCapabilities Capabilities { get; } = new(
        "instagram", [ContentKind.Video], ["reel"], "instagram-reel-options/v1", 1,
        "{\"shareToFeed\":true}", true, false);
    public bool RequiresConnectedAccount => true;

    public void Validate(Content content, TargetIntent target)
    {
        if (content.Kind != ContentKind.Video || content.MediaAssets.Count != 1 || content.Title is not null)
            throw new ArgumentException("Instagram Reel requires exactly one video and no title.");
        var asset = content.MediaAssets[0];
        if (asset.DetectedMime != "video/mp4" || asset.SizeBytes is <= 0 or > MaximumVideoBytes)
            throw new ArgumentException("Instagram Reel requires a nonempty MP4 no larger than 1 GiB.");
        if (target.Visibility != "reel" || target.OptionsSchema != "instagram-reel-options/v1" || target.OptionsVersion != 1)
            throw new ArgumentException("Instagram Reel target options are invalid.");
        if (content.Text is { Length: > 2200 }) throw new ArgumentException("Instagram caption is too long.");
        _ = ShareToFeed(target.CanonicalOptionsJson);
    }

    public Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken)
    {
        Validate(input.Content, input.Target);
        InstagramReelCheckpoint state;
        try { state = checkpoint is null ? new() : JsonSerializer.Deserialize<InstagramReelCheckpoint>(checkpoint) ?? throw new JsonException(); }
        catch (JsonException) { state = new(); return Task.FromResult(Plan("invalid-checkpoint", input, state)); }
        var operation = state.MediaId is not null ? "cleanup" : state.StageOperationId is null ? "stage" :
            state.ContainerId is null ? "create" : state.ContainerFinished && state.FinishedCheckedAt is { } checkedAt &&
                checkedAt.Add(FinishedSnapshotLifetime) > time.GetUtcNow() ? "publish" : "poll";
        return Task.FromResult(Plan(operation, input, state));
    }

    public async Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        InstagramReelPlan plan;
        try { plan = JsonSerializer.Deserialize<InstagramReelPlan>(providerStep.OpaquePlan ?? "") ?? throw new JsonException(); }
        catch (JsonException) { return Attention("instagram_plan_invalid"); }
        if (plan.Operation == "invalid-checkpoint") return Attention("instagram_checkpoint_invalid");
        try
        {
            if (plan.Operation == "stage") return await StageAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Operation == "cleanup") return await CleanupAsync(plan, cancellationToken).ConfigureAwait(false);
            TokenMaterial token;
            AccountConnection? account;
            try
            {
                token = await auth.GetValidTokenAsync(plan.Input.Publication.AccountId, cancellationToken).ConfigureAwait(false);
                account = (await store.GetAccountConnectionsAsync(cancellationToken).ConfigureAwait(false))
                    .SingleOrDefault(value => value.AccountId == plan.Input.Publication.AccountId && value.Provider == "instagram");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return Attention("instagram_auth_required"); }
            if (account is null || string.IsNullOrWhiteSpace(account.RemoteSubject)) return Attention("instagram_auth_required");
            return plan.Operation switch
            {
                "create" => await CreateAsync(plan, account.RemoteSubject, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "poll" => await PollAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "publish" => await PublishAsync(plan, account.RemoteSubject, token.AccessToken, cancellationToken).ConfigureAwait(false),
                _ => Attention("instagram_plan_invalid"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InstagramPublishingException error) { return Failure(plan, error); }
        catch (TemporaryPublicMediaException) { return StageOrCleanupFailure(plan); }
        catch (InvalidOperationException) { return StageOrCleanupFailure(plan); }
        catch (CryptographicException) { return plan.Operation == "cleanup" ? StageOrCleanupFailure(plan) : Attention("instagram_auth_required"); }
        catch (KeyNotFoundException)
        {
            return plan.Operation == "cleanup" ? StageOrCleanupFailure(plan) :
                Attention(plan.Operation == "create" ? "instagram_staging_missing" : "instagram_auth_required");
        }
        catch (IOException) { return StageOrCleanupFailure(plan); }
        catch (UnauthorizedAccessException) { return StageOrCleanupFailure(plan); }
    }

    public async Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken)
    {
        InstagramReelCheckpoint state;
        try { state = checkpoint is null ? new() : JsonSerializer.Deserialize<InstagramReelCheckpoint>(checkpoint) ?? throw new JsonException(); }
        catch (JsonException) { return Attention("instagram_checkpoint_invalid"); }
        if (state.MediaId is not null)
            return Continue(state, JobKind.Poll, PublicationState.Processing, time.GetUtcNow());
        if (state.ContainerId is null)
            return Attention("instagram_container_result_unknown");
        try
        {
            var token = await auth.GetValidTokenAsync(input.Publication.AccountId, cancellationToken).ConfigureAwait(false);
            var status = await client.GetContainerStatusAsync(token.AccessToken, state.ContainerId, cancellationToken).ConfigureAwait(false);
            // PUBLISHED proves publication but the official status response does not yield its media ID.
            // FINISHED cannot prove that a timed-out media_publish was never applied.
            return status switch
            {
                "PUBLISHED" => Attention("instagram_publish_result_unverifiable"),
                "FINISHED" => Attention("instagram_publish_result_unverifiable"),
                "IN_PROGRESS" => Attention("instagram_publish_result_unverifiable"),
                "ERROR" => Attention("instagram_container_processing_failed"),
                "EXPIRED" => Attention("instagram_container_expired"),
                _ => Attention("instagram_container_status_unknown"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is InstagramPublishingException or InvalidOperationException or CryptographicException)
        {
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: checkpoint,
                SafeError: "instagram_reconcile_unavailable", RetryAt: time.GetUtcNow().AddMinutes(5),
                FailureCategory: FailureCategory.Network);
        }
    }

    private async Task<StepResult> StageAsync(InstagramReelPlan plan, CancellationToken cancellationToken)
    {
        var asset = plan.Input.Content.MediaAssets[0];
        if (!await spool.VerifyAsync(asset, cancellationToken).ConfigureAwait(false))
            return Attention("instagram_media_changed");
        using var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        var operation = await media.StageAsync(plan.Input.Publication.Id, asset.StorageRef, host.Value, cancellationToken).ConfigureAwait(false);
        if (operation.Status != "Staged" || operation.PublicUrl is null)
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect,
                SafeError: "instagram_staging_pending", RetryAt: time.GetUtcNow().AddSeconds(30),
                FailureCategory: FailureCategory.Network);
        var next = plan.Checkpoint with { StageOperationId = operation.Id };
        return Continue(next, JobKind.Publish, PublicationState.Ready, time.GetUtcNow());
    }

    private async Task<StepResult> CreateAsync(InstagramReelPlan plan, string accountId, string token, CancellationToken cancellationToken)
    {
        var staged = await media.GetAsync(plan.Checkpoint.StageOperationId!.Value, cancellationToken).ConfigureAwait(false);
        if (staged.Status != "Staged" || !Uri.TryCreate(staged.PublicUrl, UriKind.Absolute, out var url))
            return Attention("instagram_staging_missing");
        var id = await client.CreateContainerAsync(accountId, token, url,
            plan.Input.Content.Text ?? string.Empty, ShareToFeed(plan.Input.Target.CanonicalOptionsJson), cancellationToken).ConfigureAwait(false);
        var next = plan.Checkpoint with { ContainerId = id, ContainerCreatedAt = time.GetUtcNow() };
        return Continue(next, JobKind.Poll, PublicationState.Processing, time.GetUtcNow().Add(PollInterval))
            with
        { RemoteObjectId = id };
    }

    private async Task<StepResult> PollAsync(InstagramReelPlan plan, string token, CancellationToken cancellationToken)
    {
        var status = await client.GetContainerStatusAsync(token, plan.Checkpoint.ContainerId!, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            "FINISHED" => Continue(plan.Checkpoint with { ContainerFinished = true, FinishedCheckedAt = time.GetUtcNow() }, JobKind.Publish,
                PublicationState.Ready, time.GetUtcNow()),
            "IN_PROGRESS" when plan.Checkpoint.ContainerCreatedAt is { } created &&
                time.GetUtcNow() - created < PollWindow =>
                Continue(plan.Checkpoint, JobKind.Poll, PublicationState.Processing, time.GetUtcNow().Add(PollInterval)),
            "IN_PROGRESS" => Attention("instagram_container_timeout"),
            "ERROR" => Attention("instagram_container_processing_failed"),
            "EXPIRED" => Attention("instagram_container_expired"),
            "PUBLISHED" => Attention("instagram_publish_result_unverifiable"),
            _ => Attention("instagram_container_status_unknown"),
        };
    }

    private async Task<StepResult> PublishAsync(InstagramReelPlan plan, string accountId, string token, CancellationToken cancellationToken)
    {
        if (plan.Checkpoint.ContainerId is null || !plan.Checkpoint.ContainerFinished)
            return Attention("instagram_container_not_ready");
        if (plan.Checkpoint.FinishedCheckedAt is not { } checkedAt ||
            checkedAt.Add(FinishedSnapshotLifetime) <= time.GetUtcNow())
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect,
                Checkpoint: JsonSerializer.Serialize(plan.Checkpoint), RetryAt: time.GetUtcNow(),
                ObservedState: PublicationState.Processing, NextJobKind: JobKind.Poll);
        var id = await client.PublishAsync(accountId, token, plan.Checkpoint.ContainerId, cancellationToken).ConfigureAwait(false);
        var next = plan.Checkpoint with { MediaId = id };
        // The media ID and cleanup continuation commit in the same transaction.
        return Continue(next, JobKind.Poll, PublicationState.Processing, time.GetUtcNow()) with { RemoteObjectId = id };
    }

    private async Task<StepResult> CleanupAsync(InstagramReelPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Checkpoint.MediaId is null) return Attention("instagram_media_id_missing");
        if (plan.Checkpoint.StageOperationId is { } operationId)
        {
            try { _ = await media.GetAsync(operationId, cancellationToken).ConfigureAwait(false); }
            catch (KeyNotFoundException) { return Published(plan.Checkpoint); } // Prior delete committed before a crash.
            using var host = await HostAsync(cancellationToken).ConfigureAwait(false);
            await media.DeleteAsync(operationId, host.Value, cancellationToken).ConfigureAwait(false);
        }
        return Published(plan.Checkpoint);
    }

    private static StepResult Published(InstagramReelCheckpoint checkpoint) =>
        new(StepOutcome.Completed, EffectCertainty.Confirmed,
            Checkpoint: JsonSerializer.Serialize(checkpoint), ObservedState: PublicationState.Published);

    private StepResult Failure(InstagramReelPlan plan, InstagramPublishingException error)
    {
        var checkpoint = JsonSerializer.Serialize(plan.Checkpoint);
        if (error.Ambiguous && plan.Operation is "create" or "publish")
            return new(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, Checkpoint: checkpoint,
                SafeError: plan.Operation == "publish" ? "instagram_publish_ambiguous" : "instagram_container_create_ambiguous",
                FailureCategory: FailureCategory.Unknown);
        if (plan.Operation == "poll" && (error.Ambiguous || error.Status is null || error.Status == HttpStatusCode.TooManyRequests))
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: checkpoint,
                SafeError: error.Code, RetryAt: time.GetUtcNow().AddMinutes(1), FailureCategory: FailureCategory.Network);
        if (plan.Operation == "create" && error.Status == HttpStatusCode.TooManyRequests)
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: checkpoint,
                SafeError: error.Code, RetryAt: time.GetUtcNow().AddMinutes(5), FailureCategory: FailureCategory.RateLimit);
        return Attention(error.Code);
    }

    private StepResult StageOrCleanupFailure(InstagramReelPlan plan) =>
        plan.Operation == "cleanup"
            ? Continue(plan.Checkpoint, JobKind.Poll, PublicationState.Processing, time.GetUtcNow().AddMinutes(5)) with
            { SafeError = "instagram_cleanup_failed", FailureCategory = FailureCategory.Network }
            : new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: "instagram_staging_failed",
                RetryAt: time.GetUtcNow().AddMinutes(1), FailureCategory: FailureCategory.Network);

    private static StepResult Continue(InstagramReelCheckpoint checkpoint, JobKind job, PublicationState state, DateTimeOffset at) =>
        new(StepOutcome.Pending, EffectCertainty.Confirmed, Checkpoint: JsonSerializer.Serialize(checkpoint),
            RetryAt: at, ObservedState: state, NextJobKind: job);

    private static StepResult Attention(string code) =>
        new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: code,
            ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.Provider);

    private ProviderStep Plan(string operation, ProviderPublication input, InstagramReelCheckpoint checkpoint)
    {
        var effect = operation switch
        {
            "stage" => StepEffect.UploadOnly,
            "create" => StepEffect.CreateRemoteObject,
            "publish" => StepEffect.MayPublish,
            "cleanup" => StepEffect.DeleteExisting,
            _ => StepEffect.ReadOnly,
        };
        var replay = operation switch
        {
            "create" or "publish" => ReplaySafety.NotReplayable,
            "stage" or "cleanup" => ReplaySafety.SafeRepeatNoPublication,
            _ => ReplaySafety.SafeRead,
        };
        var key = "instagram.reel." + operation + ".v1";
        return new(key, effect, replay, input.Publication.Id.ToString("N") + ":" + operation,
            time.GetUtcNow(), OpaquePlan: JsonSerializer.Serialize(new InstagramReelPlan(operation, input, checkpoint)));
    }

    private static bool ShareToFeed(string options)
    {
        try
        {
            using var doc = JsonDocument.Parse(options);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("shareToFeed", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }
        catch (JsonException) { }
        throw new ArgumentException("Instagram Reel shareToFeed option is required.");
    }

    private async Task<HostLease> HostAsync(CancellationToken cancellationToken) =>
        new(await createHost(cancellationToken).ConfigureAwait(false));

    private readonly struct HostLease(ITemporaryPublicMediaHost value) : IDisposable
    {
        public ITemporaryPublicMediaHost Value { get; } = value;
        public void Dispose() => (Value as IDisposable)?.Dispose();
    }
}
