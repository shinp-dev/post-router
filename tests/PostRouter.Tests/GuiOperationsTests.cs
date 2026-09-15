using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Tests;

public sealed class GuiOperationsTests
{
    [Fact]
    public async Task Dashboard_list_and_detail_are_projected_from_durable_state()
    {
        await using var context = await TestContext.CreateAsync();
        var immediate = await context.Posts.EnqueueAsync(context.Intent("gui-immediate"));
        _ = await context.Posts.EnqueueAsync(context.Intent("gui-scheduled", due: context.Time.GetUtcNow().AddHours(2)));

        Assert.Equal(1, await context.Worker.RunOnceAsync());

        var dashboard = await context.Operations.DashboardAsync();
        Assert.Equal(1, dashboard.Published);
        Assert.Equal(1, dashboard.Scheduled);
        var publications = await context.Operations.PublicationsAsync();
        Assert.Equal(2, publications.Count);
        var published = publications.Single(item => item.PostId == immediate.PostId);
        Assert.Equal(PublicationState.Published, published.PublicationState);
        Assert.NotNull(published.RemoteId);
        var detail = await context.Operations.PublicationAsync(published.PublicationId);
        Assert.NotNull(detail);
        Assert.Equal("hello", detail.Text);
        Assert.False(detail.CanRetry);
        Assert.False(detail.CanReconcile);
    }

    [Fact]
    public async Task Manual_retry_only_requeues_a_confirmed_no_side_effect_failure()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(new StepResult(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "fixture_rejected"));
        var queued = await context.Posts.EnqueueAsync(context.Intent("gui-retry"));
        _ = await context.Worker.RunOnceAsync();
        var publicationId = Assert.Single(queued.PublicationIds);

        Assert.True((await context.Operations.PublicationAsync(publicationId))!.CanRetry);
        await context.Operations.RetryAsync(publicationId);
        Assert.Equal(PublicationState.Ready, (await context.Operations.PublicationAsync(publicationId))!.Summary.PublicationState);
        _ = await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Published, (await context.Operations.PublicationAsync(publicationId))!.Summary.PublicationState);
        Assert.Equal(2, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Unknown_exposes_reconcile_without_enabling_retry()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: "response_lost"));
        var queued = await context.Posts.EnqueueAsync(context.Intent("gui-unknown"));
        _ = await context.Worker.RunOnceAsync();
        var publicationId = Assert.Single(queued.PublicationIds);

        var detail = await context.Operations.PublicationAsync(publicationId);
        Assert.NotNull(detail);
        Assert.Equal(PublicationState.Unknown, detail.Summary.PublicationState);
        Assert.True(detail.CanReconcile);
        Assert.True(detail.ReconcileQueued);
        Assert.False(detail.CanRetry);
        await context.Operations.ReconcileAsync(publicationId);
        _ = await context.Worker.RunOnceAsync();
        Assert.Equal(1, context.Provider.PublishCalls);
    }

    [Fact]
    public async Task Safe_needs_attention_can_retry_but_ambiguous_history_cannot()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.QueuePublish(new StepResult(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
            SafeError: "auth_required", ObservedState: PublicationState.NeedsAttention));
        var queued = await context.Posts.EnqueueAsync(context.Intent("gui-auth-retry"));
        _ = await context.Worker.RunOnceAsync();
        var publicationId = Assert.Single(queued.PublicationIds);

        Assert.True((await context.Operations.PublicationAsync(publicationId))!.CanRetry);
        await context.Operations.RetryAsync(publicationId);
        _ = await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Published, (await context.Operations.PublicationAsync(publicationId))!.Summary.PublicationState);

        context.Provider.QueuePublish(new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: "response_lost"));
        context.Provider.QueueReconcile(new StepResult(StepOutcome.Rejected, EffectCertainty.NoSideEffect,
            SafeError: "manual_attention", ObservedState: PublicationState.NeedsAttention));
        var ambiguous = await context.Posts.EnqueueAsync(context.Intent("gui-ambiguous-attention"));
        _ = await context.Worker.RunOnceAsync();
        _ = await context.Worker.RunOnceAsync();
        var ambiguousId = Assert.Single(ambiguous.PublicationIds);
        Assert.False((await context.Operations.PublicationAsync(ambiguousId))!.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Operations.RetryAsync(ambiguousId));
    }

    [Fact]
    public async Task Enqueue_text_resolves_provider_from_account_and_rejects_unknown_account()
    {
        await using var context = await TestContext.CreateAsync();
        _ = await context.Posts.EnqueueAsync(context.Intent("seed-account", due: context.Time.GetUtcNow().AddHours(1)));

        var queued = await context.Operations.EnqueueTextAsync(new(context.AccountId, "from gui", ClientRequestId: "gui-create"));

        Assert.False(queued.Existing);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            context.Operations.EnqueueTextAsync(new(Guid.NewGuid(), "wrong account", ClientRequestId: "wrong-account")));
    }
}
