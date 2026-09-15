using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed class ApprovalAwarePostRouterStore(
    IPostRouterStore inner,
    IPublicationApprovalStore approvals,
    TimeProvider timeProvider,
    SqliteDatabase database) : IPostRouterStore
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
    public Task<EnqueueResult> EnqueueAsync(CanonicalPostIntent intent, byte[] canonicalBytes, string hash, CancellationToken cancellationToken = default) => inner.EnqueueAsync(intent, canonicalBytes, hash, cancellationToken);
    public Task<PostSummary?> GetPostAsync(Guid postId, CancellationToken cancellationToken = default) => inner.GetPostAsync(postId, cancellationToken);
    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default) => inner.GetAccountsAsync(cancellationToken);
    public Task<IReadOnlyList<AccountConnection>> GetAccountConnectionsAsync(CancellationToken cancellationToken = default) => inner.GetAccountConnectionsAsync(cancellationToken);
    public Task<DashboardSummary> GetDashboardAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.GetDashboardAsync(now, cancellationToken);
    public Task<IReadOnlyList<PublicationListItem>> GetPublicationsAsync(int limit, CancellationToken cancellationToken = default) => inner.GetPublicationsAsync(limit, cancellationToken);
    public Task<PublicationDetail?> GetPublicationDetailAsync(Guid publicationId, CancellationToken cancellationToken = default) => inner.GetPublicationDetailAsync(publicationId, cancellationToken);
    public Task<bool> RequestRetryAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.RequestRetryAsync(publicationId, now, cancellationToken);
    public Task<bool> RequestReconcileAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.RequestReconcileAsync(publicationId, now, cancellationToken);

    public async Task<int> RequestCancelAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        var changed = await inner.RequestCancelAsync(postId, cancellationToken).ConfigureAwait(false);
        changed += await approvals.CancelAwaitingApprovalForPostAsync(postId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken = default) => inner.GetQueueAsync(cancellationToken);
    public Task<Guid> StartWorkerRunAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.StartWorkerRunAsync(now, cancellationToken);
    public Task StopWorkerRunAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.StopWorkerRunAsync(runId, now, cancellationToken);
    public Task RecoverAbandonedClaimsAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.RecoverAbandonedClaimsAsync(runId, now, cancellationToken);
    public Task<IReadOnlyList<PublicationWorkItem>> ClaimDueAsync(Guid runId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default) => inner.ClaimDueAsync(runId, now, limit, cancellationToken);

    public async Task<Attempt?> PrepareDispatchAsync(PublicationWorkItem item, ProviderStep providerStep, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await approvals.TryEnterPublicationBoundaryAsync(item, providerStep, now, cancellationToken).ConfigureAwait(false)) return null;
        return await inner.PrepareDispatchAsync(item, providerStep, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitResultAsync(PublicationWorkItem item, Attempt attempt, StepResult result, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var commitItem = item;
        if (result.Outcome == StepOutcome.Pending && !result.ConsumesRetryBudget)
        {
            if (result.EffectCertainty == EffectCertainty.Ambiguous)
                throw new InvalidOperationException("Ambiguous progress cannot bypass the retry budget.");

            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var changed = await SqliteDatabase.ExecuteCountAsync(connection, transaction,
                "UPDATE jobs SET attempt_count=attempt_count-1 WHERE id=$id AND state='Claimed' AND attempt_count>0",
                cancellationToken, ("$id", item.Job.Id.ToString("D"))).ConfigureAwait(false);
            if (changed != 1) throw new InvalidOperationException("Progress retry-budget accounting lost the claimed job.");
            transaction.Commit();

            // SqliteStore evaluates the pending retry limit from the pre-dispatch work item.
            // A non-consuming progress receipt must never trip that failure-only limit.
            commitItem = item with { Job = item.Job with { AttemptNo = 0 } };
        }

        await inner.CommitResultAsync(commitItem, attempt, result, now, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsStopRequestedAsync(CancellationToken cancellationToken = default) => inner.IsStopRequestedAsync(cancellationToken);
    public Task RequestStopAsync(CancellationToken cancellationToken = default) => inner.RequestStopAsync(cancellationToken);
    public Task ClearStopRequestAsync(CancellationToken cancellationToken = default) => inner.ClearStopRequestAsync(cancellationToken);
    public Task SaveStatsAsync(StatsWrite write, CancellationToken cancellationToken = default) => inner.SaveStatsAsync(write, cancellationToken);
    public Task<IReadOnlyList<MetricsSnapshot>> GetStatsAsync(Guid accountId, string subjectRef, CancellationToken cancellationToken = default) => inner.GetStatsAsync(accountId, subjectRef, cancellationToken);
    public Task<int> PurgeExpiredRawAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.PurgeExpiredRawAsync(now, cancellationToken);
    public Task<AuthGrantRecord?> GetAuthGrantAsync(Guid grantId, CancellationToken cancellationToken = default) => inner.GetAuthGrantAsync(grantId, cancellationToken);
    public Task<AuthGrantRecord?> GetAuthGrantForAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => inner.GetAuthGrantForAccountAsync(accountId, cancellationToken);
    public Task SaveAuthGrantAsync(AuthGrantRecord grant, CancellationToken cancellationToken = default) => inner.SaveAuthGrantAsync(grant, cancellationToken);
    public Task<bool> ReplaceAuthGrantAsync(Guid id, long expectedGeneration, string vaultBlobId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => inner.ReplaceAuthGrantAsync(id, expectedGeneration, vaultBlobId, expiresAt, cancellationToken);
    public Task<AccountConnection?> GetAccountConnectionAsync(Guid accountId, CancellationToken cancellationToken = default) => inner.GetAccountConnectionAsync(accountId, cancellationToken);
    public Task<AccountConnection> SaveConnectedAccountAsync(AccountConnectionWrite write, CancellationToken cancellationToken = default) => inner.SaveConnectedAccountAsync(write, cancellationToken);
    public Task<bool> DisconnectAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => inner.DisconnectAccountAsync(accountId, cancellationToken);
    public Task SetQuarantineAsync(bool enabled, CancellationToken cancellationToken = default) => inner.SetQuarantineAsync(enabled, cancellationToken);
    public Task<bool> IsQuarantinedAsync(CancellationToken cancellationToken = default) => inner.IsQuarantinedAsync(cancellationToken);
    public Task<RestoreStatus> GetRestoreStatusAsync(CancellationToken cancellationToken = default) => inner.GetRestoreStatusAsync(cancellationToken);
    public Task SuppressRestoredPublicationAsync(Guid publicationId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.SuppressRestoredPublicationAsync(publicationId, reason, now, cancellationToken);
    public Task ReleaseQuarantineAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.ReleaseQuarantineAsync(now, cancellationToken);
    public Task<IReadOnlyList<DoctorCheck>> CheckAsync(CancellationToken cancellationToken = default) => inner.CheckAsync(cancellationToken);
}
