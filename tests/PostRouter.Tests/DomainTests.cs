using PostRouter.Domain;
using PostRouter.Application;

namespace PostRouter.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Canonical_intent_is_stable_across_target_order_and_identity_noise()
    {
        var accountA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var accountB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var schedule = new ScheduleIntent(ScheduleMode.Immediate, DateTimeOffset.Parse("2026-09-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromMinutes(15));
        CanonicalPostIntent Build(Guid contentId, IReadOnlyList<TargetIntent> targets, DateTimeOffset due) =>
            new("key", new Content(contentId, ContentKind.TextOnly, "  A\r\nあ  ", null, []), targets, schedule with { DueAtUtc = due });
        var targets = new[] { new TargetIntent(accountB, "fake", "public", "fake/v1", 1, "{}"), new TargetIntent(accountA, "fake", "public", "fake/v1", 1, "{}") };
        var first = CanonicalIntent.Serialize(Build(Guid.NewGuid(), targets, DateTimeOffset.Parse("2026-09-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        var second = CanonicalIntent.Serialize(Build(Guid.NewGuid(), targets.Reverse().ToArray(), DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(first, second);
        Assert.Equal(CanonicalIntent.Hash(first), CanonicalIntent.Hash(second));
    }

    [Fact]
    public void Max_lateness_changes_identity()
    {
        var account = Guid.NewGuid();
        CanonicalPostIntent Build(int seconds) => new("key", new Content(Guid.NewGuid(), ContentKind.TextOnly, "x", null, []), [new(account, "fake", "public", "fake/v1", 1, "{}")], new(ScheduleMode.Immediate, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(seconds)));
        Assert.NotEqual(CanonicalIntent.Hash(CanonicalIntent.Serialize(Build(10))), CanonicalIntent.Hash(CanonicalIntent.Serialize(Build(20))));
    }

    [Theory]
    [InlineData(PublicationState.Ready, PublicationState.Publishing, true)]
    [InlineData(PublicationState.Ready, PublicationState.Failed, true)]
    [InlineData(PublicationState.Unknown, PublicationState.Published, true)]
    [InlineData(PublicationState.Published, PublicationState.Publishing, false)]
    [InlineData(PublicationState.Expired, PublicationState.Pending, false)]
    public void Publication_transitions_are_closed(PublicationState from, PublicationState to, bool expected) => Assert.Equal(expected, PublicationStateMachine.CanTransition(from, to));

    [Fact]
    public void Retry_policy_uses_bounded_full_jitter_window()
    {
        var policy = new FullJitterRetryPolicy();
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var next = policy.NextAttempt(now, attempt);
            Assert.InRange(next, now, now.AddMinutes(5));
        }
    }
}
