namespace PostRouter.Domain;

public enum ContentKind { TextOnly, ImageSet, Video }
public enum ScheduleMode { Immediate, AtTime }
public enum ExecutionMode { Local, Native, HumanCompletion, PolicyBlocked }
public enum PublicationState
{
    Pending, Preparing, Ready, Publishing, Processing, ScheduledRemote, Published,
    Unknown, AwaitingUser, NeedsAttention, Failed, Expired, CancelRequested, Cancelled
}
public enum JobState { Queued, Claimed, Done, Blocked, Cancelled }
public enum JobKind { Publish, Prepare, Poll, Reconcile, Delete, Refresh, StatsCollect, Purge }
public enum JobOwnerType { Publication, AuthGrant, StatsSyncRun, DataDeletion }
public enum DispatchState { Prepared, ReceiptCommitted }
public enum StepEffect { ReadOnly, UploadOnly, CreateRemoteObject, MayPublish, UpdateExisting, DeleteExisting }
public enum ReplaySafety { SafeRead, ResumeKnownHandle, IdempotentExistingObject, NotReplayable }
public enum EffectCertainty { NotSent, NoSideEffect, Confirmed, Ambiguous }
public enum StepOutcome { Completed, Pending, Rejected, Ambiguous }
public enum FailureCategory { Authentication, RateLimit, Network, Provider, InvalidInput, Unknown }
public enum MetricValueStatus { Available, Unsupported, NotAuthorized, NotYetAvailable, NotReturned, Redacted, Error }

public sealed record MediaAsset(
    Guid Id, string Sha256, long SizeBytes, string DetectedMime, string StorageRef,
    TimeSpan? Duration = null, int? Width = null, int? Height = null);

public sealed record Content(
    Guid Id, ContentKind Kind, string? Text, string? Title, IReadOnlyList<MediaAsset> MediaAssets);

public sealed record TargetIntent(
    Guid AccountId, string ProviderKey, string Visibility, string OptionsSchema, int OptionsVersion, string CanonicalOptionsJson,
    string? AccountAlias = null);

public sealed record Account(Guid Id, string ProviderKey, string Alias, string Status);

public sealed record ScheduleIntent(
    ScheduleMode Mode, DateTimeOffset DueAtUtc, TimeSpan MaxLateness,
    string? RequestedLocalTime = null, string? ZoneId = null, TimeSpan? SelectedOffset = null,
    string? ZoneRulesFingerprint = null);

public sealed record CanonicalPostIntent(
    string ClientRequestId, Content Content, IReadOnlyList<TargetIntent> Targets, ScheduleIntent Schedule);

public sealed record Publication(
    Guid Id, Guid PostId, Guid TargetId, Guid AccountId, string ProviderKey,
    PublicationState State, ExecutionMode ExecutionMode, DateTimeOffset DueAtUtc,
    TimeSpan MaxLateness, DateTimeOffset CreatedAt, DateTimeOffset? FirstSubmittedAt = null,
    DateTimeOffset? ConfirmedAt = null, DateTimeOffset? PublishedAt = null, string? SafeError = null,
    long Generation = 0);

public sealed record Job(
    Guid Id, JobKind Kind, JobOwnerType OwnerType, Guid OwnerId, int Priority,
    DateTimeOffset DueAt, JobState State, long Generation, int AttemptNo,
    Guid? WorkerRunId = null, DateTimeOffset? ClaimedAt = null, string StepKey = "initial");

public sealed record Attempt(
    Guid Id, Guid JobId, string StepKey, DispatchState DispatchState, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string RequestDigest, StepEffect Effect, ReplaySafety ReplaySafety,
    EffectCertainty EffectCertainty, string? ReceiptRef = null, string? SafeError = null);

public sealed record ProviderStep(
    string StepKey, StepEffect Effect, ReplaySafety ReplaySafety, string RequestDigest,
    DateTimeOffset EarliestAt, DateTimeOffset? LatestAt = null, string? OpaquePlan = null);

public sealed record StepResult(
    StepOutcome Outcome, EffectCertainty EffectCertainty, string? RemoteObjectId = null,
    string? Checkpoint = null, string? SafeError = null, DateTimeOffset? RetryAt = null,
    PublicationState? ObservedState = null, FailureCategory? FailureCategory = null);

public sealed record EnqueueResult(Guid PostId, bool Existing, IReadOnlyList<Guid> PublicationIds);

public sealed record MetricObservation(
    string ProviderMetricKey, string? CanonicalKey, int DefinitionVersion, decimal? DecimalValue,
    string? TextValue, string Unit, MetricValueStatus Status, string DimensionsJson = "{}");

public sealed record MetricsSnapshot(
    Guid Id, Guid AccountId, string Provider, string SubjectRef, string ApiVersion,
    DateTimeOffset RetrievedAt, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd,
    string DimensionsJson, string MappingVersion, IReadOnlyList<MetricObservation> Observations);
