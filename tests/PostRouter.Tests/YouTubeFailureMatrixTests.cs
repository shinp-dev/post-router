using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class YouTubeFailureMatrixTests
{
    [Fact]
    public async Task Processing_progress_does_not_consume_failure_retry_budget()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "processing.mp4");
        var polls = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session("processing-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "processing-session")) return Task.FromResult(ResumeIncomplete());
            if (IsDataUpload(request, "processing-session"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-processing\"}"));
            if (request.Method == HttpMethod.Get)
            {
                var current = Interlocked.Increment(ref polls);
                return Task.FromResult(current <= 12
                    ? Processing("video-processing")
                    : Processed("video-processing", "private"));
            }
            return Task.FromResult(Published("video-processing"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-processing-progress", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        for (var index = 0; index < 12; index++)
        {
            Assert.Equal(1, await setup.Worker.RunOnceAsync());
            var queue = Assert.Single(await setup.Posts.QueueAsync());
            Assert.Equal(0, queue.AttemptNo);
            context.Time.Advance(TimeSpan.FromSeconds(31));
        }

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(13, polls);
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Abandoned_upload_claim_queries_remote_offset_before_resending_data()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "crash-resume.mp4");
        var statusQueries = 0;
        var dataUploads = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session("crash-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "crash-session"))
            {
                Interlocked.Increment(ref statusQueries);
                return Task.FromResult(ResumeIncomplete(1));
            }
            if (IsDataUpload(request, "crash-session"))
            {
                Interlocked.Increment(ref dataUploads);
                Assert.Equal("bytes 2-5/6", request.Content?.Headers.ContentRange?.ToString());
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-crash-resume\"}"));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Processed("video-crash-resume", "private"));
            return Task.FromResult(Published("video-crash-resume"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-crash-resume", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());

        var abandonedRun = await setup.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        var item = Assert.Single(await setup.Store.ClaimDueAsync(abandonedRun, context.Time.GetUtcNow(), 1));
        var step = await setup.Adapter.PlanNextStepAsync(item.Input, item.Checkpoint, default);
        Assert.Equal("youtube.safe-resume-upload.v1", step.StepKey);
        Assert.Equal(ReplaySafety.ResumeKnownHandle, step.ReplaySafety);
        Assert.NotNull(await setup.Store.PrepareDispatchAsync(item, step, context.Time.GetUtcNow()));
        await setup.Store.StopWorkerRunAsync(abandonedRun, context.Time.GetUtcNow());

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, statusQueries);
        Assert.Equal(1, dataUploads);
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Expired_upload_session_is_rebuilt_without_duplicate_publication()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "expired.mp4");
        var starts = 0;
        var uploads = 0;
        var statusQueries = 0;
        var publishes = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var start = Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session(start == 1 ? "expired-session" : "replacement-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "expired-session"))
            {
                Interlocked.Increment(ref statusQueries);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (IsStatusQuery(request, "replacement-session"))
            {
                Interlocked.Increment(ref statusQueries);
                return Task.FromResult(ResumeIncomplete());
            }
            if (IsDataUpload(request, "replacement-session"))
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-after-expiry\"}"));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Processed("video-after-expiry", "private"));
            Interlocked.Increment(ref publishes);
            return Task.FromResult(Published("video-after-expiry"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-expired-session", media.Path, media.Bytes));

        for (var run = 0; run < 6; run++)
        {
            context.Time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(1, await setup.Worker.RunOnceAsync());
        }

        var saved = await setup.Posts.GetAsync(queued.PostId);
        Assert.Equal(PublicationState.Published, Assert.Single(saved!.Publications).State);
        Assert.Equal("video-after-expiry", Assert.Single(saved.RemoteObjects).ProviderObjectId);
        Assert.Equal(2, starts);
        Assert.Equal(2, statusQueries);
        Assert.Equal(1, uploads);
        Assert.Equal(1, publishes);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Permanent_processing_failure_stops_without_reupload_or_publish()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "processing-failed.mp4");
        var starts = 0;
        var uploads = 0;
        var publishes = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session("processing-failed-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "processing-failed-session")) return Task.FromResult(ResumeIncomplete());
            if (IsDataUpload(request, "processing-failed-session"))
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-processing-failed\"}"));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"items\":[{\"id\":\"video-processing-failed\",\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"private\"},\"processingDetails\":{\"processingStatus\":\"failed\"},\"suggestions\":{\"processingErrors\":[\"invalidVideoFormat\"]}}]}"));
            Interlocked.Increment(ref publishes);
            return Task.FromResult(Published("video-processing-failed"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-processing-failed", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(0, await setup.Worker.RunOnceAsync());

        var publication = Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications);
        Assert.Equal(PublicationState.NeedsAttention, publication.State);
        Assert.StartsWith("youtube_processing_failed_", publication.SafeError, StringComparison.Ordinal);
        Assert.Equal(1, starts);
        Assert.Equal(1, uploads);
        Assert.Equal(0, publishes);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Publish_rate_limit_retries_known_video_without_reupload()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "rate-limit.mp4");
        var starts = 0;
        var uploads = 0;
        var publishes = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session("rate-limit-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "rate-limit-session")) return Task.FromResult(ResumeIncomplete());
            if (IsDataUpload(request, "rate-limit-session"))
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-rate-limit\"}"));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Processed("video-rate-limit", "private"));

            var publish = Interlocked.Increment(ref publishes);
            if (publish == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
                return Task.FromResult(limited);
            }
            return Task.FromResult(Published("video-rate-limit"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-rate-limit", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        var afterLimit = Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications);
        Assert.Equal(PublicationState.Publishing, afterLimit.State);
        Assert.NotEqual(PublicationState.Unknown, afterLimit.State);

        context.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, starts);
        Assert.Equal(1, uploads);
        Assert.Equal(2, publishes);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Malformed_upload_success_is_terminal_before_processing_or_publish()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "malformed-success.mp4");
        var calls = 0;
        using var http = Client((request, _, _) =>
        {
            Interlocked.Increment(ref calls);
            if (request.Method == HttpMethod.Post)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = Session("malformed-success-session");
                return Task.FromResult(response);
            }
            if (IsStatusQuery(request, "malformed-success-session")) return Task.FromResult(ResumeIncomplete());
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-malformed-upload-success", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(0, await setup.Worker.RunOnceAsync());

        var publication = Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications);
        Assert.Equal(PublicationState.Failed, publication.State);
        Assert.Equal("youtube_upload_success_malformed", publication.SafeError);
        Assert.Equal(3, calls);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public void Consent_auth_wrapper_requests_consent_without_exposing_verifier()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = new YouTubeConsentAuthProvider(new YouTubeAuthProvider(new YouTubeApiClient(http, time), time));
        var session = provider.BeginAuthorization("desktop-client", new Uri("http://127.0.0.1:9876/callback"));

        Assert.Contains("prompt=consent", session.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", session.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(session.CodeVerifier, session.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
    }

    private static async Task<YouTubeSetup> BuildAsync(TestContext context, HttpClient http)
    {
        var client = new YouTubeApiClient(http, context.Time);
        var authProvider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "youtube-matrix-auth-locks")), context.Gate, [authProvider], context.Time);
        var account = await SaveConnectionAsync(context);
        IProviderAdapter adapter = new YouTubeResumeSafeAdapter(new YouTubeProviderAdapter(auth, client, context.Time));
        var registry = new ProviderRegistry([adapter]);
        var applicationStore = new ApprovalAwarePostRouterStore(context.Store, context.Approvals, context.Time, context.Database);
        var posts = new PostService(applicationStore, context.Gate, registry, context.Time);
        var worker = new WorkerService(applicationStore, registry,
            new FileWorkerLockFactory(Path.Combine(context.Directory, "youtube-matrix-worker.lock")), context.Gate, context.Time,
            accountOperationLocks: new NoOpAccountOperationLockFactory());
        return new(account, posts, worker, adapter, applicationStore);
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context)
    {
        var expires = context.Time.GetUtcNow().AddHours(2);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("youtube-access", "youtube-refresh", expires));
        var blob = await context.Store.PutAsync("auth-token", bytes);
        return await context.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-matrix", "channel-matrix", "YouTube Matrix", "desktop-client",
            YouTubeAuthProvider.RequiredScope, expires, blob));
    }

    private static CanonicalPostIntent Intent(TestContext context, Guid accountId, string key, string path, byte[] bytes)
    {
        var asset = new MediaAsset(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, "video/mp4", path);
        return new(key, new Content(Guid.NewGuid(), ContentKind.Video, "description", "Video title", [asset]),
            [new TargetIntent(accountId, "youtube", "public", "youtube-options/v1", 1, "{}", "youtube-matrix")],
            new ScheduleIntent(ScheduleMode.Immediate, context.Time.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    private static async Task<(string Path, byte[] Bytes)> VideoAsync(TestContext context, string name)
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var path = Path.Combine(context.Directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, bytes);
    }

    private static Uri Session(string id) => new($"https://www.googleapis.com/upload/youtube/v3/videos?upload_id={id}");

    private static bool IsStatusQuery(HttpRequestMessage request, string session) =>
        request.Method == HttpMethod.Put && request.RequestUri?.Query.Contains(session, StringComparison.Ordinal) == true &&
        request.Content?.Headers.ContentLength == 0;

    private static bool IsDataUpload(HttpRequestMessage request, string session) =>
        request.Method == HttpMethod.Put && request.RequestUri?.Query.Contains(session, StringComparison.Ordinal) == true &&
        request.Content?.Headers.ContentLength > 0;

    private static HttpResponseMessage ResumeIncomplete(long? lastByte = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308);
        if (lastByte is not null) response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{lastByte.Value}");
        return response;
    }

    private static HttpResponseMessage Processing(string id) => Json(HttpStatusCode.OK,
        $"{{\"items\":[{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"uploaded\",\"privacyStatus\":\"private\"}},\"processingDetails\":{{\"processingStatus\":\"processing\"}}}}]}}");

    private static HttpResponseMessage Processed(string id, string privacy) => Json(HttpStatusCode.OK,
        $"{{\"items\":[{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"processed\",\"privacyStatus\":\"{privacy}\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true}},\"processingDetails\":{{\"processingStatus\":\"succeeded\"}}}}]}}");

    private static HttpResponseMessage Published(string id) => Json(HttpStatusCode.OK,
        $"{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"processed\",\"privacyStatus\":\"public\"}}}}");

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

    private sealed record YouTubeSetup(
        AccountConnection Account,
        PostService Posts,
        WorkerService Worker,
        IProviderAdapter Adapter,
        IPostRouterStore Store);
}
