using PostRouter.Domain;

namespace PostRouter.Tests;

public sealed class ProgressRetryBudgetTests
{
    [Fact]
    public async Task Normal_progress_does_not_exhaust_failure_retry_budget()
    {
        await using var context = await TestContext.CreateAsync();
        var progress = Enumerable.Range(0, 12)
            .Select(_ => new StepResult(
                StepOutcome.Pending,
                EffectCertainty.Confirmed,
                RetryAt: context.Time.GetUtcNow(),
                ConsumesRetryBudget: false))
            .ToArray();
        context.Provider.QueuePublish(progress);
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "normal-progress"));

        for (var i = 0; i < progress.Length; i++)
        {
            Assert.Equal(1, await context.Worker.RunOnceAsync());
            var queue = Assert.Single(await context.Posts.QueueAsync());
            Assert.Equal(0, queue.AttemptNo);
            Assert.Equal(PublicationState.Publishing, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        }

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(13, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Failure_retry_limit_counts_only_consuming_pending_results()
    {
        await using var context = await TestContext.CreateAsync();
        var progress = Enumerable.Range(0, 7)
            .Select(_ => new StepResult(
                StepOutcome.Pending,
                EffectCertainty.Confirmed,
                RetryAt: context.Time.GetUtcNow(),
                ConsumesRetryBudget: false));
        var failures = Enumerable.Range(0, 8)
            .Select(_ => new StepResult(
                StepOutcome.Pending,
                EffectCertainty.NoSideEffect,
                SafeError: "temporary_failure",
                RetryAt: context.Time.GetUtcNow(),
                FailureCategory: FailureCategory.Network));
        context.Provider.QueuePublish([.. progress, .. failures]);
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "progress-then-failures"));

        for (var i = 0; i < 14; i++) Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Publishing, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        var publication = Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications);
        Assert.Equal(PublicationState.NeedsAttention, publication.State);
        Assert.Equal("retry_limit_reached", publication.SafeError);
        Assert.Equal(15, context.Provider.PublishCalls);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
    }
}
