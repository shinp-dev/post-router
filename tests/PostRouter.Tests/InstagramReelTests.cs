using System.Net;
using System.Text;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class InstagramReelTests
{
    [Fact]
    public async Task Reel_stages_polls_publishes_once_saves_both_ids_and_cleans_up()
    {
        await using var setup = await Fixture.CreateAsync((request, call, _) =>
        {
            Assert.Equal("graph.instagram.com", request.RequestUri!.Host);
            Assert.Equal("instagram-token-marker", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("instagram-token-marker", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(call switch
            {
                1 => Json(HttpStatusCode.OK, "{\"id\":\"222\"}"),
                2 => Json(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"),
                3 => Json(HttpStatusCode.OK, "{\"id\":\"333\"}"),
                _ => throw new InvalidOperationException("Unexpected Meta request")
            });
        });
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); // durable staging operation
        Assert.Equal(1, setup.Host.StageCount);
        await setup.RunAsync(); // container create, durable ID
        Assert.Equal("222", (await setup.Context.Store.GetPublicationDetailAsync(id))!.ContainerId);
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync(); // FINISHED
        await setup.RunAsync(); // media_publish, durable media ID
        var beforeCleanup = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal("333", beforeCleanup!.Summary.RemoteId);
        Assert.Equal(PublicationState.Processing, beforeCleanup.Summary.PublicationState);
        await setup.RunAsync(); // cleanup only
        var complete = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.Published, complete!.Summary.PublicationState);
        Assert.Equal("333", complete.Summary.RemoteId);
        Assert.Equal("222", complete.ContainerId);
        Assert.Equal(1, setup.Host.StageCount);
        Assert.Equal(1, setup.Host.DeleteCount);
        Assert.Empty(await setup.Media.ListAsync());
        Assert.Equal(1, setup.PublishingRequests);
    }

    [Fact]
    public async Task Lost_publish_response_reconciles_read_only_and_never_republishes()
    {
        await using var setup = await Fixture.CreateAsync((request, call, _) =>
        {
            if (call == 1) return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"222\"}"));
            if (call == 2) return Task.FromResult(Json(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"));
            if (call == 3) throw new HttpRequestException("secret-and-response-body-marker");
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"status_code\":\"PUBLISHED\"}"));
        });
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync(); await setup.RunAsync();
        Assert.Equal(PublicationState.Unknown, (await setup.Context.Store.GetPublicationDetailAsync(id))!.Summary.PublicationState);
        await setup.RunAsync(); // read-only reconcile
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.Equal("instagram_publish_result_unverifiable", detail.Summary.SafeError);
        Assert.False(detail.CanRetry);
        Assert.Null(detail.Summary.RemoteId);
        Assert.Equal(1, setup.PublishingRequests);
        Assert.Equal(0, setup.Host.DeleteCount);
        Assert.DoesNotContain("secret-and-response-body-marker", detail.ProviderError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_container_response_never_creates_another_container()
    {
        await using var setup = await Fixture.CreateAsync((_, _, _) => throw new HttpRequestException("raw-secret-marker"));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync(); await setup.RunAsync();
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.False(detail.CanRetry);
        Assert.Equal(1, setup.ContainerRequests);
        Assert.DoesNotContain("raw-secret-marker", detail.ProviderError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_staging_response_recovers_existing_asset_without_second_upload()
    {
        await using var setup = await Fixture.CreateAsync((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"222\"}")));
        setup.Host.LoseStageResponseOnce = true;
        var id = await setup.EnqueueAsync();
        await setup.RunAsync();
        Assert.Equal(1, setup.Host.StageCount);
        setup.Context.Time.Advance(TimeSpan.FromSeconds(30));
        await setup.RunAsync();
        await setup.RunAsync();
        Assert.Equal("222", (await setup.Context.Store.GetPublicationDetailAsync(id))!.ContainerId);
        Assert.Equal(1, setup.Host.StageCount);
    }

    [Fact]
    public async Task Failed_staging_operation_does_not_upload_again_without_remote_evidence()
    {
        await using var setup = await Fixture.CreateAsync((_, _, _) => throw new InvalidOperationException("Meta request unexpected"));
        setup.Host.FailStageBeforeUploadOnce = true;
        var id = await setup.EnqueueAsync();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await setup.RunAsync();
            setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        }
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.Equal(1, setup.Host.StageCount);
        Assert.Equal(0, setup.ContainerRequests);
    }

    [Fact]
    public async Task Processing_timeout_stops_without_publish_and_preserves_staging()
    {
        await using var setup = await Fixture.CreateAsync((_, call, _) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.OK, "{\"id\":\"222\"}")
            : Json(HttpStatusCode.OK, "{\"status_code\":\"IN_PROGRESS\"}")));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        for (var minute = 0; minute < 5; minute++)
        {
            setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
            await setup.RunAsync();
        }
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.Equal("instagram_container_timeout", detail.Summary.SafeError);
        Assert.Equal(0, setup.PublishingRequests);
        Assert.Single(await setup.Media.ListAsync());
    }

    [Fact]
    public async Task Container_rate_limit_retries_after_delay_without_reuploading_stage()
    {
        await using var setup = await Fixture.CreateAsync((_, call, _) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"raw\"}}")
            : Json(HttpStatusCode.OK, "{\"id\":\"222\"}")));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        Assert.Equal(1, setup.ContainerRequests);
        Assert.Equal(1, setup.Host.StageCount);
        setup.Context.Time.Advance(TimeSpan.FromMinutes(5));
        await setup.RunAsync();
        Assert.Equal("222", (await setup.Context.Store.GetPublicationDetailAsync(id))!.ContainerId);
        Assert.Equal(2, setup.ContainerRequests);
        Assert.Equal(1, setup.Host.StageCount);
    }

    [Fact]
    public async Task Stale_finished_snapshot_is_refreshed_before_publish()
    {
        await using var setup = await Fixture.CreateAsync((request, call, _) => Task.FromResult(call switch
        {
            1 => Json(HttpStatusCode.OK, "{\"id\":\"222\"}"),
            2 or 3 => Json(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"),
            4 => Json(HttpStatusCode.OK, "{\"id\":\"333\"}"),
            _ => throw new InvalidOperationException("Unexpected Meta request")
        }));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync(); // FINISHED
        setup.Context.Time.Advance(TimeSpan.FromMinutes(2));
        await setup.RunAsync(); // read-only refresh
        Assert.Equal(0, setup.PublishingRequests);
        await setup.RunAsync(); // publish with fresh evidence
        Assert.Equal("333", (await setup.Context.Store.GetPublicationDetailAsync(id))!.Summary.RemoteId);
        Assert.Equal(1, setup.PublishingRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_retries_only_delete_after_media_id_was_saved(bool missingCredential)
    {
        await using var setup = await Fixture.CreateAsync((_, call, _) => Task.FromResult(call switch
        {
            1 => Json(HttpStatusCode.OK, "{\"id\":\"222\"}"),
            2 => Json(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"),
            3 => Json(HttpStatusCode.OK, "{\"id\":\"333\"}"),
            _ => throw new InvalidOperationException("Unexpected Meta request")
        }));
        setup.Host.FailDeleteOnce = !missingCredential;
        setup.Host.FailDeleteWithMissingCredentialOnce = missingCredential;
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync(); await setup.RunAsync();
        await setup.RestartWorkerAsync(); // media ID was committed before the new process takes the cleanup job
        await setup.RunAsync();
        var pendingCleanup = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal("333", pendingCleanup!.Summary.RemoteId);
        Assert.Equal(PublicationState.Processing, pendingCleanup.Summary.PublicationState);
        Assert.Equal(1, setup.PublishingRequests);
        setup.Context.Time.Advance(TimeSpan.FromMinutes(5));
        await setup.RunAsync();
        Assert.Equal(PublicationState.Published, (await setup.Context.Store.GetPublicationDetailAsync(id))!.Summary.PublicationState);
        Assert.Equal(1, setup.PublishingRequests);
        Assert.Equal(2, setup.Host.DeleteCount);
    }

    [Theory]
    [InlineData("ERROR", "instagram_container_processing_failed")]
    [InlineData("EXPIRED", "instagram_container_expired")]
    [InlineData("UNRECOGNIZED", "instagram_container_status_unknown")]
    public async Task Container_terminal_or_unknown_status_stops_before_publish(string status, string code)
    {
        await using var setup = await Fixture.CreateAsync((_, call, _) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.OK, "{\"id\":\"222\"}")
            : Json(HttpStatusCode.OK, $"{{\"status_code\":\"{status}\"}}")));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync();
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.Equal(code, detail.Summary.SafeError);
        Assert.Equal(0, setup.PublishingRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "instagram_publish_failed")]
    [InlineData(HttpStatusCode.Unauthorized, "instagram_auth_required")]
    [InlineData(HttpStatusCode.TooManyRequests, "instagram_rate_limited")]
    public async Task Publish_http_rejection_does_not_retry_without_new_evidence(HttpStatusCode status, string code)
    {
        await using var setup = await Fixture.CreateAsync((_, call, _) => Task.FromResult(call switch
        {
            1 => Json(HttpStatusCode.OK, "{\"id\":\"222\"}"),
            2 => Json(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"),
            _ => Json(status, "{\"error\":{\"message\":\"secret marker\"}}")
        }));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync(); await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromMinutes(1));
        await setup.RunAsync(); await setup.RunAsync();
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal(PublicationState.NeedsAttention, detail!.Summary.PublicationState);
        Assert.Equal(code, detail.Summary.SafeError);
        Assert.False(detail.CanRetry);
        Assert.Equal(1, setup.PublishingRequests);
        Assert.DoesNotContain("secret marker", detail.ProviderError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_token_stops_before_container_request_with_auth_required()
    {
        await using var setup = await Fixture.CreateAsync((_, _, _) => throw new InvalidOperationException("Meta request unexpected"));
        var id = await setup.EnqueueAsync();
        await setup.RunAsync();
        setup.Context.Time.Advance(TimeSpan.FromDays(61));
        await setup.RunAsync();
        var detail = await setup.Context.Store.GetPublicationDetailAsync(id);
        Assert.Equal("instagram_auth_required", detail!.Summary.SafeError);
        Assert.Equal(0, setup.ContainerRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "instagram_container_create_failed")]
    [InlineData(HttpStatusCode.Unauthorized, "instagram_auth_required")]
    [InlineData(HttpStatusCode.TooManyRequests, "instagram_rate_limited")]
    public async Task Container_http_errors_use_fixed_safe_codes(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(status,
            "{\"error\":{\"message\":\"token-and-caption-marker\"}}"))));
        var client = new InstagramPublishingClient(http);
        var failure = await Assert.ThrowsAsync<InstagramPublishingException>(() =>
            client.CreateContainerAsync("123", "token-marker", new Uri("https://github.com/o/r/releases/download/tag/a.mp4"),
                "caption-marker", true, CancellationToken.None));
        Assert.Equal(expected, failure.Code);
        Assert.DoesNotContain("token-and-caption-marker", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token-marker", failure.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Guid _accountId;
        private int _metaCalls;

        private Fixture(TestContext context, FakeMediaHost host, PublicMediaOperations media, Guid accountId)
        { Context = context; Host = host; Media = media; _accountId = accountId; }

        public TestContext Context { get; }
        public FakeMediaHost Host { get; }
        public PublicMediaOperations Media { get; }
        public int ContainerRequests { get; private set; }
        public int PublishingRequests { get; private set; }

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> metaResponse)
        {
            var context = await TestContext.CreateAsync();
            using var oauthHttp = new HttpClient(new Handler((request, call, _) => Task.FromResult(call switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":[{\"access_token\":\"short-token\",\"permissions\":\"instagram_business_basic,instagram_business_content_publish\"}]}"),
                2 => Json(HttpStatusCode.OK, "{\"access_token\":\"instagram-token-marker\",\"expires_in\":5184000}"),
                3 => Json(HttpStatusCode.OK, "{\"user_id\":\"123\",\"username\":\"creator\"}"),
                _ => throw new InvalidOperationException("Unexpected OAuth request")
            })));
            var authProvider = new InstagramAuthProvider(new InstagramApiClient(oauthHttp), context.Time);
            var auth = new AuthCoordinator(context.Store, context.Store,
                new FileAuthGrantLockFactory(Path.Combine(context.Directory, "instagram-grant-locks")), context.Gate,
                [authProvider], context.Time);
            var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate,
                new NoOpAccountOperationLockFactory(), [authProvider], auth);
            var session = accounts.BeginConnect("instagram", "123456", new Uri(InstagramAuthProvider.RedirectUrl));
            var account = await accounts.CompleteConnectAsync(session, "code", session.State, "app-secret-marker");
            var host = new FakeMediaHost();
            var media = new PublicMediaOperations(new TemporaryPublicMediaPayloadStore(context.Directory),
                new FilePublicMediaOperationStore(context.Directory));
            var spool = new SpoolStore(Path.Combine(context.Directory, "spool"));
            var fixture = new Fixture(context, host, media, account.AccountId);
            var metaHttp = new HttpClient(new Handler((request, call, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/media", StringComparison.Ordinal)) fixture.ContainerRequests++;
                if (request.RequestUri.AbsolutePath.EndsWith("/media_publish", StringComparison.Ordinal)) fixture.PublishingRequests++;
                fixture._metaCalls++;
                return metaResponse(request, fixture._metaCalls, token);
            }));
            var adapter = new InstagramReelProviderAdapter(auth, context.Store, spool, media,
                _ => Task.FromResult<ITemporaryPublicMediaHost>(host), new InstagramPublishingClient(metaHttp), context.Time);
            fixture._adapter = adapter;
            var providers = new ProviderRegistry([adapter]);
            var posts = new PostService(context.Store, context.Gate, providers, context.Time);
            var worker = new WorkerService(context.Store, providers,
                new FileWorkerLockFactory(Path.Combine(context.Directory, "instagram-worker.lock")), context.Gate, context.Time);
            var operations = new OperationsService(context.Store, context.Gate, providers, posts, context.Time);
            fixture._workerField = worker; fixture._operationsField = operations;
            return fixture;
        }

        // Assigned after the fake HTTP handler has captured the fixture.
        private WorkerService? _workerField;
        private OperationsService? _operationsField;

        public async Task<Guid> EnqueueAsync()
        {
            var source = Path.Combine(Context.Directory, "clip.mp4");
            await File.WriteAllBytesAsync(source, [0, 0, 0, 12, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0]);
            var asset = await new SpoolStore(Path.Combine(Context.Directory, "spool")).ImportAsync(source);
            var result = await _operationsField!.EnqueueInstagramReelAsync(new(_accountId, asset, "caption", true));
            return Assert.Single(result.PublicationIds);
        }

        public Task<int> RunAsync() => _workerField!.RunOnceAsync();
        public async Task RestartWorkerAsync()
        {
            await _workerField!.DisposeAsync();
            _workerField = new WorkerService(Context.Store, new ProviderRegistry([_adapter!]),
                new FileWorkerLockFactory(Path.Combine(Context.Directory, "instagram-worker.lock")), Context.Gate, Context.Time);
        }
        public async ValueTask DisposeAsync() { await _workerField!.DisposeAsync(); await Context.DisposeAsync(); }
        private InstagramReelProviderAdapter? _adapter;
    }

    private sealed class FakeMediaHost : ITemporaryPublicMediaHost
    {
        private StagedPublicAsset? _staged;
        public int StageCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool LoseStageResponseOnce { get; set; }
        public bool FailStageBeforeUploadOnce { get; set; }
        public bool FailDeleteOnce { get; set; }
        public bool FailDeleteWithMissingCredentialOnce { get; set; }
        public PublicMediaStagingOperation Prepare(MediaAsset asset, DateTimeOffset? expiresAt = null) => new("fake-operation");
        public Task<StagedPublicAsset> StageAsync(MediaAsset asset, PublicMediaStagingOperation operation, CancellationToken cancellationToken = default)
        {
            StageCount++;
            if (FailStageBeforeUploadOnce) { FailStageBeforeUploadOnce = false; throw new TemporaryPublicMediaException("github_stage_failed"); }
            _staged = new(new Uri("https://github.com/o/r/releases/download/tag/a.mp4"), new("fake-handle"),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), asset.Sha256, asset.SizeBytes);
            if (LoseStageResponseOnce) { LoseStageResponseOnce = false; throw new TemporaryPublicMediaException("github_stage_response_lost"); }
            return Task.FromResult(_staged);
        }
        public Task<StagedPublicAsset?> RecoverAsync(PublicMediaStagingOperation operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(_staged);
        public Task DeleteAsync(StagedPublicAssetHandle handle, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (FailDeleteOnce) { FailDeleteOnce = false; throw new TemporaryPublicMediaException("github_delete_failed"); }
            if (FailDeleteWithMissingCredentialOnce) { FailDeleteWithMissingCredentialOnce = false; throw new KeyNotFoundException("credential unavailable"); }
            _staged = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, Interlocked.Increment(ref _calls), cancellationToken);
    }
}
