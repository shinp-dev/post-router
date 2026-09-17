using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class YouTubeAdapterHardeningTests
{
    [Fact]
    public void Registered_adapter_requires_made_for_kids_and_upload_notice_acknowledgement()
    {
        var inner = new YouTubeProviderAdapter(null!, null!, TimeProvider.System);
        var adapter = new YouTubeResumeSafeAdapter(inner);
        var asset = new MediaAsset(Guid.NewGuid(), "sha", 4, "video/mp4", "unused.mp4");
        var content = new Content(Guid.NewGuid(), ContentKind.Video, "description", "title", [asset]);
        var account = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => adapter.Validate(content,
            new TargetIntent(account, "youtube", "private", "youtube-options/v1", 1,
                "{\"madeForKids\":false}", "yt")));
        Assert.Throws<ArgumentException>(() => adapter.Validate(content,
            new TargetIntent(account, "youtube", "private", "youtube-options/v1", 1,
                "{\"uploadNoticeAcknowledged\":true}", "yt")));

        adapter.Validate(content,
            new TargetIntent(account, "youtube", "private", "youtube-options/v1", 1,
                "{\"madeForKids\":false,\"uploadNoticeAcknowledged\":true}", "yt"));
    }

    [Fact]
    public async Task Safe_resume_waits_for_308_retry_after_before_data_put()
    {
        await using var context = await TestContext.CreateAsync();
        var dataPuts = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Put && request.Content?.Headers.ContentLength == 0)
            {
                var response = new HttpResponseMessage((HttpStatusCode)308);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put) Interlocked.Increment(ref dataPuts);
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(SessionUri: Session("retry-after").AbsoluteUri, Stage: "uploading");
        var plan = Plan(setup.Account.AccountId, checkpoint);
        var step = Step("youtube.safe-resume-upload.v1", plan, context.Time.GetUtcNow());
        var expected = context.Time.GetUtcNow().AddSeconds(45);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Pending, result.Outcome);
        Assert.Equal(expected, result.RetryAt);
        Assert.Equal(0, dataPuts);
    }

    [Fact]
    public async Task Ambiguous_upload_then_expired_session_stops_instead_of_creating_replacement()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(0, request.Content?.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(
            SessionUri: Session("ambiguous-expired").AbsoluteUri,
            NextOffset: 2,
            NeedsStatusQuery: true,
            Stage: "uploading");
        var plan = Plan(setup.Account.AccountId, checkpoint) with { Operation = "query-upload" };
        var step = Step("youtube.query-upload.v1", plan, context.Time.GetUtcNow(), StepEffect.ReadOnly, ReplaySafety.SafeRead);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(EffectCertainty.Ambiguous, result.EffectCertainty);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_upload_session_expired_after_ambiguous_send", result.SafeError);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    public async Task Private_completion_rechecks_remote_visibility(string privacy)
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Processed("video-private-check", privacy));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(
            VideoId: "video-private-check",
            Stage: "ready",
            Status: new YouTubeStatusSnapshot("private", true, "youtube", true, false, null),
            StatusCheckedAt: context.Time.GetUtcNow().AddMinutes(-2));
        var plan = Plan(setup.Account.AccountId, checkpoint) with { Operation = "finish-private", Visibility = "private" };
        var step = Step("youtube.finish-private.v1", plan, context.Time.GetUtcNow(), StepEffect.ConfirmPrivate, ReplaySafety.SafeRead);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_private_visibility_mismatch", result.SafeError);
    }

    [Fact]
    public async Task Private_completion_rejects_remote_native_schedule()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(ProcessedWithPublishAt(
                "video-private-scheduled", "private", context.Time.GetUtcNow().AddHours(2)));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(VideoId: "video-private-scheduled", Stage: "ready",
            Status: new YouTubeStatusSnapshot("private", true, "youtube", true, false, null),
            StatusCheckedAt: context.Time.GetUtcNow());
        var plan = Plan(setup.Account.AccountId, checkpoint) with { Operation = "finish-private", Visibility = "private" };
        var step = Step("youtube.finish-private.v1", plan, context.Time.GetUtcNow(), StepEffect.ConfirmPrivate, ReplaySafety.SafeRead);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_native_schedule_present", result.SafeError);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    public async Task Public_visibility_still_plans_a_publish_boundary_from_ready(string visibility)
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(VideoId: "known-video", Stage: "ready",
            Status: new YouTubeStatusSnapshot(visibility, true, "youtube", true, false, null),
            StatusCheckedAt: context.Time.GetUtcNow());
        var asset = new MediaAsset(Guid.NewGuid(), "sha", 6, "video/mp4", "unused.mp4");
        var content = new Content(Guid.NewGuid(), ContentKind.Video, "description", "title", [asset]);
        var target = new TargetIntent(setup.Account.AccountId, "youtube", visibility, "youtube-options/v1", 1,
            "{\"madeForKids\":false,\"uploadNoticeAcknowledged\":true}", "youtube-hardening", ApprovalPolicy.RequireApproval);
        var publication = new Publication(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), setup.Account.AccountId,
            "youtube", PublicationState.Ready, ExecutionMode.Local, context.Time.GetUtcNow(), TimeSpan.FromMinutes(15),
            context.Time.GetUtcNow());

        var step = await setup.Adapter.PlanNextStepAsync(new(publication, content, target), JsonSerializer.Serialize(checkpoint),
            CancellationToken.None);

        Assert.Equal("youtube.publish.v1", step.StepKey);
        Assert.Equal(StepEffect.MayPublish, step.Effect);
        Assert.False(PublicationStateMachine.CanTransition(PublicationState.Ready, PublicationState.Published));
    }

    [Fact]
    public async Task Publish_refreshes_status_and_stops_if_remote_visibility_changed()
    {
        await using var context = await TestContext.CreateAsync();
        var updates = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Processed("video-external-change", "unlisted"));
            Interlocked.Increment(ref updates);
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(
            VideoId: "video-external-change",
            Stage: "ready",
            Status: new YouTubeStatusSnapshot("private", true, "youtube", true, false, null),
            StatusCheckedAt: context.Time.GetUtcNow().AddMinutes(-2));
        var plan = Plan(setup.Account.AccountId, checkpoint) with { Operation = "publish", Visibility = "public" };
        var step = Step("youtube.publish.v1", plan, context.Time.GetUtcNow(), StepEffect.MayPublish, ReplaySafety.IdempotentExistingObject);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_publish_remote_visibility_changed", result.SafeError);
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task Publish_stops_if_remote_native_schedule_is_present()
    {
        await using var context = await TestContext.CreateAsync();
        var updates = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(ProcessedWithPublishAt(
                    "video-native-schedule", "private", context.Time.GetUtcNow().AddHours(2)));
            Interlocked.Increment(ref updates);
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var setup = await BuildAsync(context, http);
        var checkpoint = new YouTubeCheckpoint(
            VideoId: "video-native-schedule",
            Stage: "ready",
            Status: new YouTubeStatusSnapshot("private", true, "youtube", true, false, null),
            StatusCheckedAt: context.Time.GetUtcNow());
        var plan = Plan(setup.Account.AccountId, checkpoint) with { Operation = "publish", Visibility = "public" };
        var step = Step("youtube.publish.v1", plan, context.Time.GetUtcNow(), StepEffect.MayPublish, ReplaySafety.IdempotentExistingObject);

        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_native_schedule_present", result.SafeError);
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task Processing_poll_has_a_durable_operational_deadline()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var setup = await BuildAsync(context, http);
        var asset = new MediaAsset(Guid.NewGuid(), "sha", 6, "video/mp4", "unused.mp4");
        var content = new Content(Guid.NewGuid(), ContentKind.Video, "description", "title", [asset]);
        var target = new TargetIntent(setup.Account.AccountId, "youtube", "public", "youtube-options/v1", 1,
            "{\"madeForKids\":false,\"uploadNoticeAcknowledged\":true}", "youtube-hardening");
        var publication = new Publication(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), setup.Account.AccountId, "youtube",
            PublicationState.Processing, ExecutionMode.Local, context.Time.GetUtcNow(), TimeSpan.FromMinutes(30),
            context.Time.GetUtcNow().AddDays(-8), FirstSubmittedAt: context.Time.GetUtcNow().AddDays(-8));
        var input = new ProviderPublication(publication, content, target);
        var checkpoint = JsonSerializer.Serialize(new YouTubeCheckpoint(VideoId: "video-long-processing", Stage: "processing"));

        var step = await setup.Adapter.PlanNextStepAsync(input, checkpoint, CancellationToken.None);
        var result = await setup.Adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal("youtube.processing-deadline.v1", step.StepKey);
        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
        Assert.Equal("youtube_processing_deadline_exceeded", result.SafeError);
    }

    private static YouTubePlan Plan(Guid accountId, YouTubeCheckpoint checkpoint) => new(
        accountId,
        "upload",
        new MediaAsset(Guid.NewGuid(), "sha", 6, "video/mp4", "unused.mp4"),
        "title",
        "description",
        "public",
        new YouTubeOptions(false, null),
        checkpoint);

    private static ProviderStep Step(
        string key,
        YouTubePlan plan,
        DateTimeOffset now,
        StepEffect effect = StepEffect.UploadOnly,
        ReplaySafety replaySafety = ReplaySafety.ResumeKnownHandle) =>
        new(key, effect, replaySafety, "digest", now, OpaquePlan: JsonSerializer.Serialize(plan));

    private static async Task<Setup> BuildAsync(TestContext context, HttpClient http)
    {
        var client = new YouTubeApiClient(http, context.Time);
        var authProvider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "youtube-hardening-auth-locks")),
            context.Gate, [authProvider], context.Time);
        var account = await SaveConnectionAsync(context);
        var adapter = new YouTubeResumeSafeAdapter(
            new YouTubeProviderAdapter(auth, client, context.Time), context.Time, auth, client);
        return new(account, adapter);
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context)
    {
        var expires = context.Time.GetUtcNow().AddHours(2);
        var material = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("access", "refresh", expires));
        var blob = await context.Store.PutAsync("auth-token", material);
        return await context.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-hardening", "channel-hardening", "YouTube Hardening", "desktop-client",
            YouTubeAuthProvider.RequiredScope, expires, blob));
    }

    private static Uri Session(string id) =>
        new($"https://www.googleapis.com/upload/youtube/v3/videos?upload_id={id}");

    private static HttpResponseMessage Processed(string id, string privacy) => Json(HttpStatusCode.OK,
        $"{{\"items\":[{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"processed\",\"privacyStatus\":\"{privacy}\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true}},\"processingDetails\":{{\"processingStatus\":\"succeeded\"}}}}]}}");

    private static HttpResponseMessage ProcessedWithPublishAt(string id, string privacy, DateTimeOffset publishAt) => Json(HttpStatusCode.OK,
        $"{{\"items\":[{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"processed\",\"privacyStatus\":\"{privacy}\",\"publishAt\":\"{publishAt:O}\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true}},\"processingDetails\":{{\"processingStatus\":\"succeeded\"}}}}]}}");

    private static HttpClient Client(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(new StubHandler(response)) { BaseAddress = new Uri("http://127.0.0.1/") };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, Interlocked.Increment(ref _calls), cancellationToken);
    }

    private sealed record Setup(AccountConnection Account, YouTubeResumeSafeAdapter Adapter);
}
