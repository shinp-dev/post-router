using PostRouter.Domain;

namespace PostRouter.Tests;

public sealed class PollingLifecycleTests
{
    [Fact]
    public async Task Successful_poll_progress_uses_fresh_jobs_without_consuming_retry_attempts()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "poll-progress"));
        var runId = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());

        try
        {
            var publish = Assert.Single(await context.Store.ClaimDueAsync(runId, context.Time.GetUtcNow(), 1));
            Assert.Equal(JobKind.Publish, publish.Job.Kind);
            var start = await context.Store.PrepareDispatchAsync(
                publish,
                ReadStep("begin-processing"),
                context.Time.GetUtcNow());
            Assert.NotNull(start);
            await context.Store.CommitResultAsync(
                publish,
                start!,
                Continue(JobKind.Poll, PublicationState.Processing, context.Time.GetUtcNow()),
                context.Time.GetUtcNow());

            for (var i = 0; i < 12; i++)
            {
                var queue = Assert.Single(await context.Posts.QueueAsync());
                Assert.Equal(JobKind.Poll, queue.Kind);
                Assert.Equal(0, queue.AttemptNo);

                var poll = Assert.Single(await context.Store.ClaimDueAsync(runId, context.Time.GetUtcNow(), 1));
                Assert.Equal(JobKind.Poll, poll.Job.Kind);
                Assert.Equal(0, poll.Job.AttemptNo);
                var attempt = await context.Store.PrepareDispatchAsync(
                    poll,
                    ReadStep($"poll-{i}"),
                    context.Time.GetUtcNow());
                Assert.NotNull(attempt);
                await context.Store.CommitResultAsync(
                    poll,
                    attempt!,
                    Continue(JobKind.Poll, PublicationState.Processing, context.Time.GetUtcNow()),
                    context.Time.GetUtcNow());
            }

            var finalPoll = Assert.Single(await context.Store.ClaimDueAsync(runId, context.Time.GetUtcNow(), 1));
            var finalAttempt = await context.Store.PrepareDispatchAsync(
                finalPoll,
                ReadStep("poll-complete"),
                context.Time.GetUtcNow());
            Assert.NotNull(finalAttempt);
            await context.Store.CommitResultAsync(
                finalPoll,
                finalAttempt!,
                Continue(JobKind.Publish, PublicationState.Ready, context.Time.GetUtcNow()),
                context.Time.GetUtcNow());

            var next = Assert.Single(await context.Posts.QueueAsync());
            Assert.Equal(JobKind.Publish, next.Kind);
            Assert.Equal(0, next.AttemptNo);
            Assert.Equal(PublicationState.Ready, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        }
        finally
        {
            await context.Store.StopWorkerRunAsync(runId, context.Time.GetUtcNow());
        }
    }

    [Fact]
    public async Task Poll_transport_failures_retry_the_same_poll_job_and_keep_the_common_retry_limit()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "poll-failure"));
        var runId = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());

        try
        {
            var publish = Assert.Single(await context.Store.ClaimDueAsync(runId, context.Time.GetUtcNow(), 1));
            var start = await context.Store.PrepareDispatchAsync(
                publish,
                ReadStep("begin-processing"),
                context.Time.GetUtcNow());
            Assert.NotNull(start);
            await context.Store.CommitResultAsync(
                publish,
                start!,
                Continue(JobKind.Poll, PublicationState.Processing, context.Time.GetUtcNow()),
                context.Time.GetUtcNow());

            Guid? pollJobId = null;
            for (var i = 0; i < 8; i++)
            {
                var poll = Assert.Single(await context.Store.ClaimDueAsync(runId, context.Time.GetUtcNow(), 1));
                Assert.Equal(JobKind.Poll, poll.Job.Kind);
                pollJobId ??= poll.Job.Id;
                Assert.Equal(pollJobId, poll.Job.Id);
                Assert.Equal(i, poll.Job.AttemptNo);

                var attempt = await context.Store.PrepareDispatchAsync(
                    poll,
                    ReadStep("poll-read"),
                    context.Time.GetUtcNow());
                Assert.NotNull(attempt);
                await context.Store.CommitResultAsync(
                    poll,
                    attempt!,
                    new StepResult(
                        StepOutcome.Pending,
                        EffectCertainty.NoSideEffect,
                        SafeError: "temporary_poll_failure",
                        RetryAt: context.Time.GetUtcNow(),
                        FailureCategory: FailureCategory.Network),
                    context.Time.GetUtcNow());
            }

            var publication = Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications);
            Assert.Equal(PublicationState.NeedsAttention, publication.State);
            Assert.Equal("retry_limit_reached", publication.SafeError);
            Assert.Empty(await context.Posts.QueueAsync());
        }
        finally
        {
            await context.Store.StopWorkerRunAsync(runId, context.Time.GetUtcNow());
        }
    }

    private static ProviderStep ReadStep(string key) =>
        new(key, StepEffect.ReadOnly, ReplaySafety.SafeRead, key, DateTimeOffset.UnixEpoch);

    private static StepResult Continue(JobKind nextJobKind, PublicationState state, DateTimeOffset dueAt) =>
        new(
            StepOutcome.Pending,
            EffectCertainty.Confirmed,
            RetryAt: dueAt,
            ObservedState: state,
            NextJobKind: nextJobKind);
}
