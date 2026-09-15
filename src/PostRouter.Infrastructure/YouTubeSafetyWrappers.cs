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

internal sealed class YouTubeResumeSafeAdapter(YouTubeProviderAdapter inner) : IProviderAdapter
{
    private const string SafeResumeStepKey = "youtube.safe-resume-upload.v1";

    public string ProviderKey => inner.ProviderKey;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public bool RequiresConnectedAccount => inner.RequiresConnectedAccount;

    public void Validate(Content content, TargetIntent target) => inner.Validate(content, target);

    public async Task<ProviderStep> PlanNextStepAsync(
        ProviderPublication input,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        var step = await inner.PlanNextStepAsync(input, checkpoint, cancellationToken).ConfigureAwait(false);
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
        if (!string.Equals(providerStep.StepKey, SafeResumeStepKey, StringComparison.Ordinal))
            return await inner.ExecuteStepAsync(providerStep, cancellationToken).ConfigureAwait(false);

        YouTubePlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<YouTubePlan>(providerStep.OpaquePlan ?? string.Empty)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
                SafeError: "youtube_resume_plan_invalid", ObservedState: PublicationState.NeedsAttention,
                FailureCategory: FailureCategory.InvalidInput);
        }

        var queryPlan = plan with { Operation = "query-upload" };
        var queryOpaque = JsonSerializer.Serialize(queryPlan);
        var queryStep = new ProviderStep(
            "youtube.query-upload.v1",
            StepEffect.ReadOnly,
            ReplaySafety.SafeRead,
            Digest("youtube.query-upload.v1", queryOpaque),
            providerStep.EarliestAt,
            providerStep.LatestAt,
            queryOpaque);
        var queryResult = await inner.ExecuteStepAsync(queryStep, cancellationToken).ConfigureAwait(false);
        if (queryResult.Outcome != StepOutcome.Pending || string.IsNullOrWhiteSpace(queryResult.Checkpoint))
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
        var uploadOpaque = JsonSerializer.Serialize(uploadPlan);
        var uploadStep = new ProviderStep(
            "youtube.upload.v1",
            StepEffect.UploadOnly,
            ReplaySafety.ResumeKnownHandle,
            Digest("youtube.upload.v1", uploadOpaque),
            providerStep.EarliestAt,
            providerStep.LatestAt,
            uploadOpaque);
        return await inner.ExecuteStepAsync(uploadStep, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StepResult> ReconcileAsync(
        ProviderPublication input,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        var result = await inner.ReconcileAsync(input, checkpoint, cancellationToken).ConfigureAwait(false);
        // A visibility update that remains unobserved should not poll forever. There is no repost
        // risk because reconciliation is read-only; after the normal retry budget the item becomes
        // NeedsAttention for a human decision.
        return string.Equals(result.SafeError, "youtube_publish_not_observed", StringComparison.Ordinal)
            ? result with { ConsumesRetryBudget = true }
            : result;
    }

    private static string Digest(string stepKey, string? opaquePlan)
    {
        var bytes = Encoding.UTF8.GetBytes($"{stepKey}\n{opaquePlan ?? string.Empty}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
