using PostRouter.Domain;

namespace PostRouter.Application;

public sealed record PublicationApprovalStatus(
    ApprovalPolicy Policy,
    bool Approved,
    DateTimeOffset? ApprovedAt,
    bool CanApprove);

public interface IPublicationApprovalStore
{
    Task<bool> TryEnterPublicationBoundaryAsync(
        PublicationWorkItem item,
        ProviderStep step,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<PublicationApprovalStatus> GetStatusAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default);

    Task<bool> ApproveAsync(
        Guid publicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<int> CancelAwaitingApprovalForPostAsync(
        Guid postId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<int> CountAwaitingApprovalAsync(CancellationToken cancellationToken = default);
}

internal sealed class PassThroughPublicationApprovalStore : IPublicationApprovalStore
{
    public static readonly PassThroughPublicationApprovalStore Instance = new();
    private PassThroughPublicationApprovalStore() { }

    public Task<bool> TryEnterPublicationBoundaryAsync(PublicationWorkItem item, ProviderStep step, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<PublicationApprovalStatus> GetStatusAsync(Guid publicationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublicationApprovalStatus(ApprovalPolicy.Automatic, true, null, false));

    public Task<bool> ApproveAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<int> CancelAwaitingApprovalForPostAsync(Guid postId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<int> CountAwaitingApprovalAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
