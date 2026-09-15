using PostRouter.Domain;

namespace PostRouter.Application;

public sealed record ProviderPublication(Publication Publication, Content Content, TargetIntent Target);
public sealed record PublicationWorkItem(Job Job, ProviderPublication Input, string? Checkpoint)
{
    public Publication Publication => Input.Publication;
}
public sealed record RemoteObjectSummary(Guid PublicationId, string Kind, string ProviderObjectId, DateTimeOffset ObservedAt);
public sealed record PostSummary(Guid PostId, string ClientRequestId, DateTimeOffset CreatedAt, IReadOnlyList<Publication> Publications, IReadOnlyList<RemoteObjectSummary> RemoteObjects);
public sealed record QueueItem(Guid JobId, JobKind Kind, JobState State, DateTimeOffset DueAt, Guid OwnerId, string ProviderKey, int AttemptNo, string? SafeError);
public sealed record RawMetricInput(string Provider, string ApiVersion, string SubjectRef, byte[] Payload, DateTimeOffset ExpiresAt);
public sealed record StatsWrite(RawMetricInput Raw, MetricsSnapshot Snapshot);
public sealed record AuthGrantRecord(
    Guid Id, string Provider, string Subject, long Generation, DateTimeOffset ExpiresAt,
    string VaultBlobId, string Status, Guid? AccountId = null, string? ClientId = null, string? Scope = null);
public sealed record TokenMaterial(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[REDACTED TOKEN MATERIAL]";
}
public sealed record RefreshResult(TokenMaterial Material, string ProviderRequestId);
public sealed record AccountConnection(
    Guid AccountId, string Provider, string Alias, string Status, string RemoteSubject,
    string DisplayName, string ClientId, string Scope, DateTimeOffset? TokenExpiresAt,
    string? LastAuthError = null);
public sealed record AccountConnectionWrite(
    string Provider, string Alias, string RemoteSubject, string DisplayName, string ClientId,
    string Scope, DateTimeOffset ExpiresAt, string VaultBlobId);
public sealed record AuthorizationSession(
    string Provider, string ClientId, Uri RedirectUri, Uri AuthorizationUri, string State,
    string CodeVerifier, string Scope, Guid? ExpectedAccountId = null, string? ExpectedSubject = null,
    string? Alias = null);
public sealed record ConnectedIdentity(string RemoteSubject, string DisplayName, string Scope, TokenMaterial Material);
public sealed record AccountRevokeResult(Guid AccountId, bool RemoteRevoked, bool LocalDisconnected, string? SafeError);
public sealed record ProviderCapabilities(
    string ProviderKey, IReadOnlyList<ContentKind> ContentKinds, IReadOnlyList<string> Visibilities,
    string OptionsSchema, int OptionsVersion, string DefaultOptionsJson,
    bool InteractiveAuthentication, bool Revoke);
public sealed record DashboardSummary(
    int Scheduled, int Pending, int Processing, int Published, int Failed, int NeedsAttention,
    int Unknown, int Cancelled, int Expired, int AuthenticationErrors);
public sealed record PublicationListItem(
    Guid PostId, Guid PublicationId, string Provider, Guid AccountId, string AccountAlias,
    string ContentPreview, PublicationState PublicationState, ScheduleMode ScheduleMode,
    DateTimeOffset DueAt, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt,
    string? RemoteId, JobKind? JobKind, JobState? JobState, DateTimeOffset? JobDueAt,
    int AttemptCount, string? SafeError);
public sealed record PublicationDetail(
    PublicationListItem Summary, string? Text, string Visibility, string OptionsSchema,
    DateTimeOffset? FirstSubmittedAt, DateTimeOffset? ConfirmedAt,
    string? ProviderError, string? NormalizedError, bool ReconcileQueued,
    bool CanCancel, bool CanRetry, bool CanReconcile);
public sealed record CreateTextPostRequest(
    Guid AccountId, string Text, DateTimeOffset? PublishAt = null,
    string? ClientRequestId = null);
public class ProviderOperationException(string safeCode, bool retryable, Exception? inner = null) : Exception(safeCode, inner)
{
    public string SafeCode { get; } = safeCode;
    public bool Retryable { get; } = retryable;
}
public sealed record DoctorCheck(string Name, bool Healthy, string Code, string Message);
public sealed record BackupResult(string Path, string Sha256, DateTimeOffset CreatedAt);
public sealed record RestorePublication(Guid PublicationId, PublicationState State, string Risk, bool HasRemoteObject);
public sealed record RestoreStatus(bool Quarantined, IReadOnlyList<RestorePublication> Publications);

public interface IPostRouterStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<EnqueueResult> EnqueueAsync(CanonicalPostIntent intent, byte[] canonicalBytes, string hash, CancellationToken cancellationToken = default);
    Task<PostSummary?> GetPostAsync(Guid postId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountConnection>> GetAccountConnectionsAsync(CancellationToken cancellationToken = default);
    Task<DashboardSummary> GetDashboardAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationListItem>> GetPublicationsAsync(int limit, CancellationToken cancellationToken = default);
    Task<PublicationDetail?> GetPublicationDetailAsync(Guid publicationId, CancellationToken cancellationToken = default);
    Task<bool> RequestRetryAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> RequestReconcileAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<int> RequestCancelAsync(Guid postId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken = default);
    Task<Guid> StartWorkerRunAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task StopWorkerRunAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecoverAbandonedClaimsAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationWorkItem>> ClaimDueAsync(Guid runId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);
    Task<Attempt?> PrepareDispatchAsync(PublicationWorkItem item, ProviderStep providerStep, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CommitResultAsync(PublicationWorkItem item, Attempt attempt, StepResult result, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> IsStopRequestedAsync(CancellationToken cancellationToken = default);
    Task RequestStopAsync(CancellationToken cancellationToken = default);
    Task ClearStopRequestAsync(CancellationToken cancellationToken = default);
    Task SaveStatsAsync(StatsWrite write, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetricsSnapshot>> GetStatsAsync(Guid accountId, string subjectRef, CancellationToken cancellationToken = default);
    Task<int> PurgeExpiredRawAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<AuthGrantRecord?> GetAuthGrantAsync(Guid grantId, CancellationToken cancellationToken = default);
    Task<AuthGrantRecord?> GetAuthGrantForAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SaveAuthGrantAsync(AuthGrantRecord grant, CancellationToken cancellationToken = default);
    Task<bool> ReplaceAuthGrantAsync(Guid id, long expectedGeneration, string vaultBlobId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<AccountConnection?> GetAccountConnectionAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountConnection> SaveConnectedAccountAsync(AccountConnectionWrite write, CancellationToken cancellationToken = default);
    Task<bool> DisconnectAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SetQuarantineAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<bool> IsQuarantinedAsync(CancellationToken cancellationToken = default);
    Task<RestoreStatus> GetRestoreStatusAsync(CancellationToken cancellationToken = default);
    Task SuppressRestoredPublicationAsync(Guid publicationId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task ReleaseQuarantineAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorCheck>> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IProviderAdapter
{
    string ProviderKey { get; }
    ProviderCapabilities Capabilities { get; }
    bool RequiresConnectedAccount { get; }
    void Validate(Content content, TargetIntent target);
    Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken);
    Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken);
    Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken);
}

public interface IProviderRegistry
{
    IProviderAdapter GetRequired(string providerKey);
    IReadOnlyList<ProviderCapabilities> GetCapabilities();
}

public interface IMetricsProvider
{
    Task<StatsWrite> FetchMetricsAsync(Guid accountId, string subjectRef, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface ISpoolStore
{
    Task<MediaAsset> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(MediaAsset asset, CancellationToken cancellationToken = default);
}

public interface IWorkerLock : IAsyncDisposable { }
public interface IWorkerLockFactory { ValueTask<IWorkerLock?> TryAcquireAsync(CancellationToken cancellationToken = default); }
public interface IRetryPolicy { DateTimeOffset NextAttempt(DateTimeOffset now, int attemptNumber); }
public interface IMaintenanceLease : IAsyncDisposable { }
public interface IMaintenanceGate
{
    ValueTask<IMaintenanceLease> AcquireSharedAsync(CancellationToken cancellationToken = default);
    ValueTask<IMaintenanceLease?> TryAcquireExclusiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IVault
{
    Task<string> PutAsync(string purpose, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);
    Task<byte[]> GetAsync(string blobId, string purpose, CancellationToken cancellationToken = default);
    Task DeleteAsync(string blobId, CancellationToken cancellationToken = default);
}

public interface IAuthProvider
{
    string ProviderKey { get; }
    Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken);
}

public interface IInteractiveAuthProvider : IAuthProvider
{
    AuthorizationSession BeginAuthorization(string clientId, Uri redirectUri, Guid? expectedAccountId = null, string? expectedSubject = null, string? requestedAlias = null);
    Task<ConnectedIdentity> CompleteAuthorizationAsync(AuthorizationSession session, string code, string returnedState, CancellationToken cancellationToken);
    Task RevokeAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken);
}

public interface IAuthGrantLock : IAsyncDisposable { }
public interface IAuthGrantLockFactory { ValueTask<IAuthGrantLock> AcquireAsync(Guid grantId, CancellationToken cancellationToken = default); }
public interface IAccountOperationLock : IAsyncDisposable { }
public interface IAccountOperationLockFactory { ValueTask<IAccountOperationLock> AcquireAsync(Guid accountId, CancellationToken cancellationToken = default); }

public interface IDatabaseMaintenance
{
    Task<BackupResult> BackupAsync(string destinationDirectory, CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorCheck>> CheckAsync(CancellationToken cancellationToken = default);
}
