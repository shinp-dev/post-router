using PostRouter.Domain;

namespace PostRouter.Tests;

public sealed class PublicationApprovalTests
{
    [Fact]
    public async Task Required_approval_blocks_publish_boundary_without_provider_call()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-block", approvalPolicy: ApprovalPolicy.RequireApproval));
        var publicationId = Assert.Single(queued.PublicationIds);

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(0, context.Provider.PublishCalls);

        var post = await context.Posts.GetAsync(queued.PostId);
        Assert.Equal(PublicationState.AwaitingApproval, Assert.Single(post!.Publications).State);
        var job = Assert.Single(await context.Posts.QueueAsync());
        Assert.Equal(JobState.Blocked, job.State);
        Assert.Equal("approval_required", job.SafeError);

        var approval = await context.Operations.PublicationApprovalAsync(publicationId);
        Assert.Equal(ApprovalPolicy.RequireApproval, approval.Policy);
        Assert.False(approval.Approved);
        Assert.True(approval.CanApprove);
    }

    [Fact]
    public async Task Approval_requeues_same_intent_and_publishes_once()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-resume", approvalPolicy: ApprovalPolicy.RequireApproval));
        var publicationId = Assert.Single(queued.PublicationIds);
        await context.Worker.RunOnceAsync();

        await context.Operations.ApproveAsync(publicationId);
        var approved = await context.Operations.PublicationApprovalAsync(publicationId);
        Assert.True(approved.Approved);
        Assert.False(approved.CanApprove);

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Automatic_policy_preserves_existing_publish_behavior()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-auto"));
        var publicationId = Assert.Single(queued.PublicationIds);

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);

        var approval = await context.Operations.PublicationApprovalAsync(publicationId);
        Assert.Equal(ApprovalPolicy.Automatic, approval.Policy);
        Assert.True(approval.Approved);
        Assert.False(approval.CanApprove);
    }

    [Fact]
    public async Task Cancel_while_awaiting_approval_never_calls_provider()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-cancel", approvalPolicy: ApprovalPolicy.RequireApproval));
        await context.Worker.RunOnceAsync();

        Assert.Equal(1, await context.Posts.CancelAsync(queued.PostId));
        Assert.Equal(PublicationState.Cancelled, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(0, context.Provider.PublishCalls);
        Assert.Empty(await context.Posts.QueueAsync());
    }

    [Fact]
    public async Task Preapproval_does_not_bypass_future_due_time()
    {
        await using var context = await TestContext.CreateAsync();
        var due = context.Time.GetUtcNow().AddMinutes(10);
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-scheduled", due: due, approvalPolicy: ApprovalPolicy.RequireApproval));
        var publicationId = Assert.Single(queued.PublicationIds);

        await context.Operations.ApproveAsync(publicationId);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(0, context.Provider.PublishCalls);

        context.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
    }

    [Fact]
    public async Task Late_approval_does_not_turn_expired_schedule_into_immediate_publish()
    {
        await using var context = await TestContext.CreateAsync();
        var due = context.Time.GetUtcNow().AddMinutes(1);
        var queued = await context.Posts.EnqueueAsync(context.Intent(
            key: "approval-late",
            due: due,
            maxLateness: TimeSpan.FromMinutes(2),
            approvalPolicy: ApprovalPolicy.RequireApproval));
        var publicationId = Assert.Single(queued.PublicationIds);

        context.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.AwaitingApproval, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);

        context.Time.Advance(TimeSpan.FromMinutes(3));
        await context.Operations.ApproveAsync(publicationId);
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        Assert.Equal(0, context.Provider.PublishCalls);
        Assert.Equal(PublicationState.Expired, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
    }

    [Fact]
    public async Task Approval_is_idempotent_for_the_same_intent_hash()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent(key: "approval-idempotent", approvalPolicy: ApprovalPolicy.RequireApproval));
        var publicationId = Assert.Single(queued.PublicationIds);
        await context.Worker.RunOnceAsync();

        await context.Operations.ApproveAsync(publicationId);
        await context.Operations.ApproveAsync(publicationId);

        Assert.Equal(1, await context.Worker.RunOnceAsync());
        Assert.Equal(1, context.Provider.PublishCalls);
    }
}
