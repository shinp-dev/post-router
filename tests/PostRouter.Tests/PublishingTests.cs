using PostRouter.Domain;

namespace PostRouter.Tests;

public sealed class PublishingTests
{
    [Fact]
    public async Task Concurrent_same_key_is_one_post_and_conflict_is_rejected()
    {
        await using var context = await TestContext.CreateAsync();
        var intent = context.Intent();
        var results = await Task.WhenAll(context.Posts.EnqueueAsync(intent), context.Posts.EnqueueAsync(intent));
        Assert.Equal(results[0].PostId, results[1].PostId);
        Assert.Single(results.Select(x => x.PostId).Distinct());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Posts.EnqueueAsync(context.Intent(text: "different")));
    }

    [Fact]
    public async Task Successful_publish_is_committed_once()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
        var post = await context.Posts.GetAsync(queued.PostId);
        Assert.Equal(PublicationState.Published, Assert.Single(post!.Publications).State);
        Assert.Single(post.RemoteObjects);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Lost_publish_response_becomes_unknown_and_is_never_republished()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: "response_lost"));
        context.Provider.QueueReconcile(new StepResult(StepOutcome.Completed, EffectCertainty.Confirmed, "remote-1", ObservedState: PublicationState.Published));
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Unknown, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Crash_after_prepared_non_replayable_dispatch_recovers_to_unknown()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        var firstRun = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        var item = Assert.Single(await context.Store.ClaimDueAsync(firstRun, context.Time.GetUtcNow(), 1));
        var providerStep = await context.Provider.PlanNextStepAsync(item.Input, null, default);
        await context.Store.PrepareDispatchAsync(item, providerStep, context.Time.GetUtcNow());
        await context.Store.StopWorkerRunAsync(firstRun, context.Time.GetUtcNow());
        var secondRun = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        await context.Store.RecoverAbandonedClaimsAsync(secondRun, context.Time.GetUtcNow());
        await context.Store.StopWorkerRunAsync(secondRun, context.Time.GetUtcNow());
        Assert.Equal(PublicationState.Unknown, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Contains(await context.Posts.QueueAsync(), x => x.Kind == JobKind.Reconcile);
        Assert.Equal(0, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Overdue_unsent_publication_expires_without_http()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(due: context.Time.GetUtcNow().AddMinutes(1), maxLateness: TimeSpan.FromMinutes(2)));
        context.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Expired, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(0, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Cancel_before_dispatch_prevents_publish()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        Assert.Equal(1, await context.Posts.CancelAsync(queued.PostId));
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Cancelled, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(0, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Cancel_after_dispatch_does_not_discard_publish_receipt()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.Delay = TimeSpan.FromMilliseconds(150);
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        var running = context.Worker.RunOnceAsync();
        Assert.True(SpinWait.SpinUntil(() => context.Provider.PublishCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await context.Posts.CancelAsync(queued.PostId));
        await running;
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Cancel_after_claim_but_before_dispatch_skips_provider_call()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        var run = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        var item = Assert.Single(await context.Store.ClaimDueAsync(run, context.Time.GetUtcNow(), 1));
        await context.Posts.CancelAsync(queued.PostId);
        var step = await context.Provider.PlanNextStepAsync(item.Input, null, default);
        Assert.Null(await context.Store.PrepareDispatchAsync(item, step, context.Time.GetUtcNow()));
        Assert.Equal(0, context.Provider.PublishCalls);
        await context.Store.StopWorkerRunAsync(run, context.Time.GetUtcNow());
    }

    [Fact]
    public async Task Cancellation_after_publish_dispatch_becomes_unknown_not_retry()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.Delay = TimeSpan.FromSeconds(5);
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "cancelled-http"));
        using var cancellation = new CancellationTokenSource();
        var running = context.Worker.RunOnceAsync(cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => context.Provider.PublishCalls == 1, TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await running;
        Assert.Equal(PublicationState.Unknown, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, context.Provider.PublishCalls);
        Assert.Contains(await context.Posts.QueueAsync(), job => job.Kind == JobKind.Reconcile);
    }

    [Fact]
    public async Task Provider_retry_at_is_not_run_early()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: "rate_limited", RetryAt: context.Time.GetUtcNow().AddMinutes(2)));
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "rate-limit"));
        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
        var waiting = Assert.Single(await context.Posts.QueueAsync());
        Assert.Equal("rate_limited", waiting.SafeError);
        context.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
    }

    [Fact]
    public async Task Retry_limit_stops_safe_retries()
    {
        await using var context = await TestContext.CreateAsync();
        var pending = Enumerable.Range(0, 8)
            .Select(_ => new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, RetryAt: context.Time.GetUtcNow()))
            .ToArray();
        context.Provider.QueuePublish(pending);
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        for (var attempt = 0; attempt < 8; attempt++) await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.NeedsAttention, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(8, context.Provider.PublishCalls);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Claimed_work_rehydrates_content_media_and_provider_options()
    {
        await using var context = await TestContext.CreateAsync();
        var asset = new MediaAsset(Guid.NewGuid(), new string('a', 64), 123, "video/mp4", Path.Combine(context.Directory, "spool", "asset.mp4"), TimeSpan.FromSeconds(5), 1080, 1920);
        var intent = new CanonicalPostIntent("rehydrate", new Content(Guid.NewGuid(), ContentKind.Video, "caption", "title", [asset]),
            [new TargetIntent(context.AccountId, "fake", "private", "fake-options/v1", 1, "{\"mode\":\"test\"}")],
            new ScheduleIntent(ScheduleMode.Immediate, context.Time.GetUtcNow(), TimeSpan.FromMinutes(15)));
        await context.Posts.EnqueueAsync(intent);
        var run = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        var work = Assert.Single(await context.Store.ClaimDueAsync(run, context.Time.GetUtcNow(), 1));
        Assert.Equal("caption", work.Input.Content.Text);
        Assert.Equal(asset.Sha256, Assert.Single(work.Input.Content.MediaAssets).Sha256);
        Assert.Equal("private", work.Input.Target.Visibility);
        Assert.Equal("{\"mode\":\"test\"}", work.Input.Target.CanonicalOptionsJson);
        await context.Store.StopWorkerRunAsync(run, context.Time.GetUtcNow());
    }

    [Fact]
    public async Task Multi_target_publish_records_partial_result_without_rolling_back_success()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(
            new StepResult(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "fixture_rejected"),
            new StepResult(StepOutcome.Completed, EffectCertainty.Confirmed, "remote-ok", ObservedState: PublicationState.Published));
        var secondAccount = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var baseIntent = context.Intent(key: "partial");
        var intent = baseIntent with { Targets = [.. baseIntent.Targets, new TargetIntent(secondAccount, "fake", "public", "fake-options/v1", 1, "{}")] };
        var queued = await context.Posts.EnqueueAsync(intent);
        Assert.Equal(2, await context.Worker.RunOnceAsync());
        var states = (await context.Posts.GetAsync(queued.PostId))!.Publications.Select(publication => publication.State).Order().ToArray();
        Assert.Equal(new[] { PublicationState.Published, PublicationState.Failed }.Order().ToArray(), states);
        Assert.Equal(2, context.Provider.PublishCalls);
    }
}
