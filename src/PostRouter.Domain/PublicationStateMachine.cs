namespace PostRouter.Domain;

public static class PublicationStateMachine
{
    private static readonly Dictionary<PublicationState, HashSet<PublicationState>> Allowed =
        new Dictionary<PublicationState, HashSet<PublicationState>>
        {
            [PublicationState.Pending] = [PublicationState.Preparing, PublicationState.Ready, PublicationState.CancelRequested, PublicationState.Expired, PublicationState.NeedsAttention],
            [PublicationState.Preparing] = [PublicationState.Ready, PublicationState.Processing, PublicationState.Unknown, PublicationState.Failed, PublicationState.NeedsAttention, PublicationState.CancelRequested],
            [PublicationState.Ready] = [PublicationState.Publishing, PublicationState.ScheduledRemote, PublicationState.CancelRequested, PublicationState.Expired, PublicationState.NeedsAttention],
            [PublicationState.Publishing] = [PublicationState.Processing, PublicationState.Published, PublicationState.Unknown, PublicationState.Failed, PublicationState.NeedsAttention, PublicationState.CancelRequested],
            [PublicationState.Processing] = [PublicationState.Published, PublicationState.Failed, PublicationState.Unknown, PublicationState.NeedsAttention, PublicationState.CancelRequested],
            [PublicationState.ScheduledRemote] = [PublicationState.Published, PublicationState.Unknown, PublicationState.NeedsAttention, PublicationState.CancelRequested],
            [PublicationState.Unknown] = [PublicationState.Processing, PublicationState.Published, PublicationState.NeedsAttention, PublicationState.Failed],
            [PublicationState.AwaitingUser] = [PublicationState.Published, PublicationState.CancelRequested, PublicationState.NeedsAttention],
            [PublicationState.NeedsAttention] = [PublicationState.Published, PublicationState.Failed, PublicationState.CancelRequested],
            [PublicationState.CancelRequested] = [PublicationState.Cancelled, PublicationState.Published, PublicationState.Unknown, PublicationState.NeedsAttention],
            [PublicationState.Failed] = [PublicationState.Pending],
        };

    public static bool CanTransition(PublicationState from, PublicationState to) =>
        from == to || Allowed.TryGetValue(from, out var values) && values.Contains(to);

    public static void EnsureCanTransition(PublicationState from, PublicationState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Publication transition {from} -> {to} is not allowed.");
    }

    public static bool CanCancel(PublicationState state) => state is
        PublicationState.Pending or PublicationState.Preparing or PublicationState.Ready or
        PublicationState.Publishing or PublicationState.Processing or PublicationState.ScheduledRemote or
        PublicationState.AwaitingUser or PublicationState.NeedsAttention;

    public static bool CanRetry(PublicationState state) => state == PublicationState.Failed ||
        state == PublicationState.NeedsAttention && CanTransition(state, PublicationState.Failed);

    public static bool CanReconcile(PublicationState state) =>
        state is PublicationState.Unknown or PublicationState.Processing or PublicationState.CancelRequested;
}
