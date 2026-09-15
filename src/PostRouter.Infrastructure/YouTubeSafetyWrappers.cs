using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

internal sealed class YouTubeConsentAuthProvider(YouTubeAuthProvider inner) : IInteractiveAuthProvider
{
    public string ProviderKey => inner.ProviderKey;

    public AuthorizationSession BeginAuthorization(
        string clientId,
        Uri redirectUri,
        Guid? expectedAccountId = null,
        string? expectedSubject = null,
        string? requestedAlias = null)
    {
        var session = inner.BeginAuthorization(clientId, redirectUri, expectedAccountId, expectedSubject, requestedAlias);
        var separator = string.IsNullOrEmpty(session.AuthorizationUri.Query) ? "?" : "&";
        return session with { AuthorizationUri = new Uri(session.AuthorizationUri.AbsoluteUri + separator + "prompt=consent") };
    }

    public Task<ConnectedIdentity> CompleteAuthorizationAsync(
        AuthorizationSession session,
        string code,
        string returnedState,
        CancellationToken cancellationToken) =>
        inner.CompleteAuthorizationAsync(session, code, returnedState, cancellationToken);

    public Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken) =>
        inner.RefreshAsync(grant, current, cancellationToken);

    public Task RevokeAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken) =>
        inner.RevokeAsync(grant, current, cancellationToken);
}

internal sealed class YouTubeResumeSafeAdapter : IProviderAdapter
{
    private const string SafeResumeStepKey = "youtube.safe-resume-upload.v1";
    private const string ProcessingDeadlineStepKey = "youtube.processing-deadline.v1";
    private static readonly TimeSpan ProcessingDeadline = TimeSpan.FromDays(7);
    private readonly YouTubeProviderAdapter _inner;
    private readonly TimeProvider _timeProvider;
    private readonly AuthCoordinator? _auth;
    private readonly YouTubeApiClient? _client;

    public YouTubeResumeSafeAdapter(YouTubeProviderAdapter inner)
        : this(inner, TimeProvider.System)
    {
    }

    public YouTubeResumeSafeAdapter(YouTubeProviderAdapter inner, TimeProvider timeProvider)
        : this(inner, timeProvider, null, null)
    {
    }

    public YouTubeResumeSafeAdapter(
        YouTubeProviderAdapter inner,
        TimeProvider timeProvider,
        AuthCoordinator? auth,
        YouTubeApiClient? client)
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _auth = auth;
        _client = client;
        Capabilities = inner.Capabilities with
        {
            DefaultOptionsJson = "{\"madeForKids\":false,\"uploadNoticeAcknowledged\":false}",
        };
    }

    public string ProviderKey => _inner.ProviderKey;
    public ProviderCapabilities Capabilities { get; }
    public bool RequiresConnectedAccount => _inner.RequiresConnectedAccount;

    public void Validate(Content content, TargetIntent target)
    {
        _inner.Validate(content, target);
        ValidatePolicyOptions(target.CanonicalOptionsJson);
    }

    public async Task<ProviderStep> PlanNextStepAsync(
        ProviderPublication input,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        Validate(input.Content, input.Target);
        var step = await _inner.PlanNextStepAsync(input, checkpoint, cancellationToken).ConfigureAwait(false);

        if (string.Equals(step.StepKey, "youtube.poll-processing.v1", StringComparison.Ordinal) &&
            input.Publication.FirstSubmittedAt is { } firstSubmitted &&
            firstSubmitted.Add(ProcessingDeadline) <= _timeProvider.GetUtcNow())
        {
            return step with
            {
                StepKey = ProcessingDeadlineStepKey,
                Effect = StepEffect.ReadOnly,
                ReplaySafety = ReplaySafety.SafeRead,
                RequestDigest = Digest(ProcessingDeadlineStepKey, step.OpaquePlan),
            };
        }

        if (!string.Equals(step.StepKey, "youtube.upload.v1", StringComparison.Ordinal)) return step;

        // A data PUT is a resumable side effect. After a process crash we cannot know how much of
        // the previous request reached Google, so the replayable unit is deliberately
        // "query remote offset, then send only the remaining bytes" rather than a blind PUT.
        return step with
        {
            StepKey = SafeResumeStepKey,
            RequestDigest = Digest(SafeResumeStepKey, step.OpaquePlan),
            ReplaySafety = ReplaySafety.ResumeKnownHandle,
        };
    }

    public async Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        if (string.Equals(providerStep.StepKey, ProcessingDeadlineStepKey, StringComparison.Ordinal))
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                SafeError: "youtube_processing_deadline_exceeded", ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.Provider);

        if (string.Equals(providerStep.StepKey, SafeResumeStepKey, StringComparison.Ordinal))
            return await ExecuteSafeResumeAsync(providerStep, cancellationToken).ConfigureAwait(false);

        if (string.Equals(providerStep.StepKey, "youtube.query-upload.v1", StringComparison.Ordinal))
            return await ExecuteGuardedQueryAsync(providerStep, cancellationToken).ConfigureAwait(false);

        if (string.Equals(providerStep.StepKey, "youtube.finish-private.v1", StringComparison.Ordinal))
            return await ExecuteVerifiedPrivateAsync(providerStep, cancellationToken).ConfigureAwait(false);

        if (string.Equals(providerStep.StepKey, "youtube.publish.v1", StringComparison.Ordinal))
            return await ExecuteFreshPublishAsync(providerStep, cancellationToken).ConfigureAwait(false);

        return await _inner.ExecuteStepAsync(providerStep, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepResult> ExecuteSafeResumeAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        if (!TryPlan(providerStep, out var plan, out var invalid)) return invalid!;

        var queryStep = BuildStep(providerStep, plan with { Operation = "query-upload" }, "youtube.query-upload.v1",
            StepEffect.ReadOnly, ReplaySafety.SafeRead);
        var queryResult = await _inner.ExecuteStepAsync(queryStep, cancellationToken).ConfigureAwait(false);
        queryResult = GuardAmbiguousSessionExpiry(plan, queryResult);
        if (queryResult.Outcome != StepOutcome.Pending || string.IsNullOrWhiteSpace(queryResult.Checkpoint))
            return queryResult;

        // YouTube explicitly asks resumable clients to honor Retry-After on 308. Do not collapse
        // the query and the following data PUT into one worker turn while that delay is active.
        if (queryResult.RetryAt is { } retryAt && retryAt > _timeProvider.GetUtcNow())
            return queryResult;

        YouTubeCheckpoint remote;
        try
        {
            remote = JsonSerializer.Deserialize<YouTubeCheckpoint>(queryResult.Checkpoint) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                SafeError: "youtube_resume_checkpoint_invalid", ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.InvalidInput);
        }

        if (remote.VideoId is not null || remote.SessionUri is null || remote.NeedsStatusQuery ||
            queryResult.SafeError is not null)
            return queryResult;
        if (remote.NextOffset < 0 || remote.NextOffset >= plan.Asset.SizeBytes)
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                Checkpoint: queryResult.Checkpoint, SafeError: "youtube_resume_offset_invalid",
                ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.InvalidInput);

        var uploadPlan = plan with { Operation = "upload", Checkpoint = remote };
        var uploadStep = BuildStep(providerStep, uploadPlan, "youtube.upload.v1",
            StepEffect.UploadOnly, ReplaySafety.ResumeKnownHandle);

        // Production is currently Windows-only. Keep a read/share-read handle open while the inner
        // adapter verifies and reopens the spool object, preventing replacement or write access in
        // the small verify-to-upload handoff window on Windows. The inner SHA/size verification is
        // still authoritative and runs immediately before the data PUT.
        await using var mediaGuard = TryOpenMediaGuard(uploadPlan.Asset);
        return await _inner.ExecuteStepAsync(uploadStep, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepResult> ExecuteGuardedQueryAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        if (!TryPlan(providerStep, out var plan, out var invalid)) return invalid!;
        var result = await _inner.ExecuteStepAsync(providerStep, cancellationToken).ConfigureAwait(false);
        return GuardAmbiguousSessionExpiry(plan, result);
    }

    private static StepResult GuardAmbiguousSessionExpiry(YouTubePlan plan, StepResult result)
    {
        if (!plan.Checkpoint.NeedsStatusQuery ||
            !string.Equals(result.SafeError, "youtube_upload_session_expired", StringComparison.Ordinal))
            return result;

        // If a prior data PUT had an ambiguous response, an expired session no longer proves that
        // no private video was finalized. Without a video ID there is no safe reconciliation key,
        // so do not create a replacement upload automatically.
        return new(StepOutcome.Rejected, EffectCertainty.Ambiguous,
            Checkpoint: JsonSerializer.Serialize(plan.Checkpoint),
            SafeError: "youtube_upload_session_expired_after_ambiguous_send",
            ObservedState: PublicationState.NeedsAttention,
            FailureCategory: FailureCategory.Unknown);
    }

    private async Task<StepResult> ExecuteVerifiedPrivateAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        if (!TryPlan(providerStep, out var plan, out var invalid)) return invalid!;
        var refreshed = await RefreshBoundaryStatusAsync(providerStep, plan, cancellationToken).ConfigureAwait(false);
        if (!TryReadyCheckpoint(refreshed, out var checkpoint)) return refreshed;

        if (!string.Equals(checkpoint.Status!.PrivacyStatus, "private", StringComparison.Ordinal))
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                Checkpoint: refreshed.Checkpoint,
                SafeError: "youtube_private_visibility_mismatch",
                ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.Provider);

        return new(StepOutcome.Completed, EffectCertainty.Confirmed, checkpoint.VideoId,
            Checkpoint: refreshed.Checkpoint, ObservedState: PublicationState.Published);
    }

    private async Task<StepResult> ExecuteFreshPublishAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        if (!TryPlan(providerStep, out var plan, out var invalid)) return invalid!;
        var refreshed = await RefreshBoundaryStatusAsync(providerStep, plan, cancellationToken).ConfigureAwait(false);
        if (!TryReadyCheckpoint(refreshed, out var checkpoint)) return refreshed;

        var currentVisibility = checkpoint.Status!.PrivacyStatus;
        if (string.Equals(currentVisibility, plan.Visibility, StringComparison.Ordinal))
            return new(StepOutcome.Completed, EffectCertainty.Confirmed, checkpoint.VideoId,
                Checkpoint: refreshed.Checkpoint, ObservedState: PublicationState.Published);

        if (!string.Equals(currentVisibility, "private", StringComparison.Ordinal))
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                Checkpoint: refreshed.Checkpoint,
                SafeError: "youtube_publish_remote_visibility_changed",
                ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.Provider);

        var freshPlan = plan with { Operation = "publish", Checkpoint = checkpoint };
        var publishStep = BuildStep(providerStep, freshPlan, "youtube.publish.v1",
            StepEffect.MayPublish, ReplaySafety.IdempotentExistingObject);
        return await _inner.ExecuteStepAsync(publishStep, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepResult> RefreshBoundaryStatusAsync(
        ProviderStep providerStep,
        YouTubePlan plan,
        CancellationToken cancellationToken)
    {
        if (_auth is null || _client is null)
            return await RefreshStatusThroughInnerAsync(providerStep, plan, cancellationToken).ConfigureAwait(false);

        TokenMaterial token;
        try
        {
            token = await _auth.GetValidTokenAsync(plan.AccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or InvalidDataException or KeyNotFoundException)
        {
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                SafeError: "youtube_boundary_auth_unavailable", ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.Authentication);
        }

        YouTubeVideoObservation observed;
        try
        {
            observed = await _client.GetVideoAsync(token.AccessToken, plan.Checkpoint.VideoId!, cancellationToken).ConfigureAwait(false);
        }
        catch (YouTubeProviderException ex)
        {
            return ex.Retryable
                ? new(StepOutcome.Pending, EffectCertainty.NoSideEffect,
                    Checkpoint: JsonSerializer.Serialize(plan.Checkpoint), SafeError: ex.SafeCode,
                    RetryAt: _timeProvider.GetUtcNow().AddMinutes(1), FailureCategory: FailureCategory.Provider)
                : new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                    Checkpoint: JsonSerializer.Serialize(plan.Checkpoint), SafeError: ex.SafeCode,
                    ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.Provider);
        }

        if (observed.Status.PublishAt is not null)
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                Checkpoint: JsonSerializer.Serialize(plan.Checkpoint),
                SafeError: "youtube_native_schedule_present",
                ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.Provider);

        if (!string.Equals(observed.Status.UploadStatus, "processed", StringComparison.Ordinal) ||
            !string.Equals(observed.ProcessingDetails?.ProcessingStatus, "succeeded", StringComparison.Ordinal))
        {
            var processing = plan.Checkpoint with { Stage = "processing" };
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect,
                Checkpoint: JsonSerializer.Serialize(processing), SafeError: "youtube_processing_regressed",
                RetryAt: _timeProvider.GetUtcNow().AddSeconds(30), FailureCategory: FailureCategory.Provider,
                ConsumesRetryBudget: false);
        }

        var snapshot = new YouTubeStatusSnapshot(
            observed.Status.PrivacyStatus ?? "private",
            observed.Status.Embeddable,
            observed.Status.License,
            observed.Status.PublicStatsViewable,
            observed.Status.SelfDeclaredMadeForKids,
            observed.Status.ContainsSyntheticMedia);
        var ready = plan.Checkpoint with
        {
            Stage = "ready",
            Status = snapshot,
            StatusCheckedAt = _timeProvider.GetUtcNow(),
        };
        return new(StepOutcome.Pending, EffectCertainty.Confirmed,
            Checkpoint: JsonSerializer.Serialize(ready), RetryAt: _timeProvider.GetUtcNow(), ConsumesRetryBudget: false);
    }

    private async Task<StepResult> RefreshStatusThroughInnerAsync(
        ProviderStep providerStep,
        YouTubePlan plan,
        CancellationToken cancellationToken)
    {
        var refreshStep = BuildStep(providerStep, plan with { Operation = "refresh-status" }, "youtube.refresh-status.v1",
            StepEffect.ReadOnly, ReplaySafety.SafeRead);
        return await _inner.ExecuteStepAsync(refreshStep, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadyCheckpoint(StepResult result, out YouTubeCheckpoint checkpoint)
    {
        checkpoint = new();
        if (result.Outcome != StepOutcome.Pending || string.IsNullOrWhiteSpace(result.Checkpoint) || result.SafeError is not null)
            return false;
        try
        {
            checkpoint = JsonSerializer.Deserialize<YouTubeCheckpoint>(result.Checkpoint) ?? new();
            return string.Equals(checkpoint.Stage, "ready", StringComparison.Ordinal) &&
                checkpoint.VideoId is not null && checkpoint.Status is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<StepResult> ReconcileAsync(
        ProviderPublication input,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        var result = await _inner.ReconcileAsync(input, checkpoint, cancellationToken).ConfigureAwait(false);
        // A visibility update that remains unobserved should not poll forever. There is no repost
        // risk because reconciliation is read-only; after the normal retry budget the item becomes
        // NeedsAttention for a human decision.
        return string.Equals(result.SafeError, "youtube_publish_not_observed", StringComparison.Ordinal)
            ? result with { ConsumesRetryBudget = true }
            : result;
    }

    private static ProviderStep BuildStep(
        ProviderStep source,
        YouTubePlan plan,
        string stepKey,
        StepEffect effect,
        ReplaySafety replaySafety)
    {
        var opaque = JsonSerializer.Serialize(plan);
        return new(stepKey, effect, replaySafety, Digest(stepKey, opaque), source.EarliestAt, source.LatestAt, opaque);
    }

    private static bool TryPlan(ProviderStep providerStep, out YouTubePlan plan, out StepResult? invalid)
    {
        try
        {
            plan = JsonSerializer.Deserialize<YouTubePlan>(providerStep.OpaquePlan ?? string.Empty)
                ?? throw new JsonException();
            invalid = null;
            return true;
        }
        catch (JsonException)
        {
            plan = null!;
            invalid = new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                SafeError: "youtube_resume_plan_invalid", ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.InvalidInput);
            return false;
        }
    }

    private static FileStream? TryOpenMediaGuard(MediaAsset asset)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(asset.StorageRef)) return null;
        try
        {
            return new FileStream(asset.StorageRef, FileMode.Open, FileAccess.Read, FileShare.Read,
                1, FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidatePolicyOptions(string canonicalOptionsJson)
    {
        try
        {
            using var options = JsonDocument.Parse(canonicalOptionsJson);
            if (options.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("YouTube options must be a JSON object.");

            bool? madeForKids = null;
            bool? uploadNoticeAcknowledged = null;
            foreach (var property in options.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "madeForKids":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("YouTube madeForKids must be boolean.");
                        madeForKids = property.Value.GetBoolean();
                        break;
                    case "uploadNoticeAcknowledged":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("YouTube uploadNoticeAcknowledged must be boolean.");
                        uploadNoticeAcknowledged = property.Value.GetBoolean();
                        break;
                }
            }

            if (madeForKids is null)
                throw new ArgumentException("YouTube madeForKids must be explicitly true or false.");
            if (uploadNoticeAcknowledged != true)
                throw new ArgumentException("YouTube uploadNoticeAcknowledged must be true before upload.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("YouTube options JSON is invalid.", ex);
        }
    }

    private static string Digest(string stepKey, string? opaquePlan)
    {
        var bytes = Encoding.UTF8.GetBytes($"{stepKey}\n{opaquePlan ?? string.Empty}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
