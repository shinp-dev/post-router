using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class XProviderTests
{
    [Fact]
    public async Task OAuth_pkce_connect_exchanges_code_and_persists_only_non_secret_metadata()
    {
        await using var context = await TestContext.CreateAsync();
        var call = 0;
        using var http = Client(async (request, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref call);
            if (current == 1)
            {
                Assert.Equal("/2/oauth2/token", request.RequestUri?.AbsolutePath);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("grant_type=authorization_code", form);
                Assert.Contains("code_verifier=", form);
                return Json(HttpStatusCode.OK, "{\"token_type\":\"bearer\",\"expires_in\":7200,\"access_token\":\"oauth-access\",\"scope\":\"tweet.read tweet.write users.read offline.access\",\"refresh_token\":\"oauth-refresh\"}");
            }
            Assert.Equal("/2/users/me", request.RequestUri?.AbsolutePath);
            Assert.Equal("oauth-access", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"123\",\"name\":\"Example\",\"username\":\"example\"}}");
        });
        var client = new XApiClient(http, context.Time);
        var provider = new XAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store, new FileAuthGrantLockFactory(Path.Combine(context.Directory, "grant-locks")), context.Gate, [provider], context.Time);
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate, new NoOpAccountOperationLockFactory(), [provider], auth);
        var session = accounts.BeginConnect("x", "client-id", new Uri("http://127.0.0.1:8765/callback"), "x-main");
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUri.Query);
        Assert.DoesNotContain(session.CodeVerifier, session.AuthorizationUri.AbsoluteUri);

        var connected = await accounts.CompleteConnectAsync(session, "authorization-code", session.State);
        Assert.Equal("123", connected.RemoteSubject);
        Assert.Equal("Connected", connected.Status);
        Assert.Equal(2, call);
        Assert.NotNull(await context.Store.GetAuthGrantForAccountAsync(connected.AccountId));
    }

    [Fact]
    public async Task Text_post_runs_through_durable_queue_and_persists_remote_id()
    {
        await using var context = await TestContext.CreateAsync();
        var requests = 0;
        using var http = Client(async (request, _, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("/2/tweets", request.RequestUri?.AbsolutePath);
            Assert.Contains("hello x", await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json(HttpStatusCode.Created, "{\"data\":{\"id\":\"1234567890\",\"text\":\"hello x\"}}");
        });
        var setup = await BuildAsync(context, http, "remote-user", "access-token", context.Time.GetUtcNow().AddHours(1));
        var intent = XIntent(context, setup.Account.AccountId, "x-e2e", "hello x");
        var queued = await setup.Posts.EnqueueAsync(intent);

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        var saved = await setup.Posts.GetAsync(queued.PostId);
        Assert.Equal(PublicationState.Published, Assert.Single(saved!.Publications).State);
        Assert.Equal("1234567890", Assert.Single(saved.RemoteObjects).ProviderObjectId);
        Assert.Equal(1, requests);
        Assert.Equal(0, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, requests);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_keeps_queue_but_blocks_send_until_same_remote_account_reconnects()
    {
        await using var context = await TestContext.CreateAsync();
        var requests = 0;
        using var http = Client((_, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Json(HttpStatusCode.Created, "{\"data\":{\"id\":\"44\"}}"));
        });
        var setup = await BuildAsync(context, http, "remote-user", "old-access", context.Time.GetUtcNow().AddHours(1));
        await setup.Posts.EnqueueAsync(XIntent(context, setup.Account.AccountId, "disconnect-queue", "wait"));
        Assert.True(await setup.Accounts.DisconnectAsync(setup.Account.AccountId));
        Assert.Equal(0, await setup.Worker.RunOnceAsync());
        Assert.Equal(0, requests);
        Assert.Contains(await context.Store.GetQueueAsync(), item => item.ProviderKey == "x" && item.State == JobState.Queued);

        var reconnected = await SaveConnectionAsync(context, "remote-user", "new-access", context.Time.GetUtcNow().AddHours(1));
        Assert.Equal(setup.Account.AccountId, reconnected.AccountId);
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, requests);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_after_claim_is_rechecked_before_dispatch()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => throw new InvalidOperationException("HTTP must not be called"));
        var setup = await BuildAsync(context, http, "111", "access", context.Time.GetUtcNow().AddHours(1));
        await setup.Posts.EnqueueAsync(XIntent(context, setup.Account.AccountId, "claimed-disconnect", "do not send"));
        var run = await context.Store.StartWorkerRunAsync(context.Time.GetUtcNow());
        var item = Assert.Single(await context.Store.ClaimDueAsync(run, context.Time.GetUtcNow(), 1));
        var providerStep = await setup.Adapter.PlanNextStepAsync(item.Input, item.Checkpoint, default);

        await setup.Accounts.DisconnectAsync(setup.Account.AccountId);
        Assert.Null(await context.Store.PrepareDispatchAsync(item, providerStep, context.Time.GetUtcNow()));
        Assert.Contains(await context.Store.GetQueueAsync(), queued => queued.JobId == item.Job.Id && queued.SafeError == "auth_disconnected");
        await context.Store.StopWorkerRunAsync(run, context.Time.GetUtcNow());
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_rejects_a_different_remote_subject_and_leaves_old_queue_paused()
    {
        await using var context = await TestContext.CreateAsync();
        var step = 0;
        using var http = Client((request, _, _) =>
        {
            var current = Interlocked.Increment(ref step);
            return Task.FromResult(current == 1
                ? Json(HttpStatusCode.OK, "{\"token_type\":\"bearer\",\"expires_in\":7200,\"access_token\":\"replacement\",\"scope\":\"tweet.read tweet.write users.read offline.access\",\"refresh_token\":\"replacement-refresh\"}")
                : Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"999\",\"name\":\"Other\",\"username\":\"other\"}}"));
        });
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        await setup.Accounts.DisconnectAsync(setup.Account.AccountId);
        var session = await setup.Accounts.BeginReconnectAsync(setup.Account.AccountId, new Uri("http://127.0.0.1:8765/callback"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Accounts.CompleteConnectAsync(session, "code", session.State));
        Assert.Equal("reconnect_account_mismatch", error.Message);
        Assert.Equal("Disconnected", (await setup.Accounts.StatusAsync(setup.Account.AccountId))!.Status);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_expired_token_reads_perform_one_refresh_and_rotate_secret()
    {
        await using var context = await TestContext.CreateAsync();
        var refreshes = 0;
        using var http = Client((request, _, _) =>
        {
            Assert.Equal("/2/oauth2/token", request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref refreshes);
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"token_type\":\"bearer\",\"expires_in\":7200,\"access_token\":\"new-access\",\"scope\":\"tweet.read tweet.write users.read offline.access\",\"refresh_token\":\"new-refresh\"}"));
        });
        var setup = await BuildAsync(context, http, "remote-user", "expired", context.Time.GetUtcNow().AddMinutes(-1));

        var tokens = await Task.WhenAll(setup.Auth.GetValidTokenAsync(setup.Account.AccountId), setup.Auth.GetValidTokenAsync(setup.Account.AccountId));
        Assert.All(tokens, token => Assert.Equal("new-access", token.AccessToken));
        Assert.Equal(1, refreshes);
        var grant = await context.Store.GetAuthGrantForAccountAsync(setup.Account.AccountId);
        Assert.Equal(1, grant!.Generation);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Revoke_calls_provider_then_erases_local_grant_without_deleting_history()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client(async (request, _, cancellationToken) =>
        {
            Assert.Equal("/2/oauth2/revoke", request.RequestUri?.AbsolutePath);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("token=refresh-token", form);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var result = await setup.Accounts.RevokeAsync(setup.Account.AccountId);

        Assert.True(result.RemoteRevoked);
        Assert.True(result.LocalDisconnected);
        Assert.Null(await context.Store.GetAuthGrantForAccountAsync(setup.Account.AccountId));
        Assert.Equal("Disconnected", (await setup.Accounts.StatusAsync(setup.Account.AccountId))!.Status);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Failed_remote_revoke_still_erases_local_credentials_and_reports_failure()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var result = await setup.Accounts.RevokeAsync(setup.Account.AccountId);

        Assert.False(result.RemoteRevoked);
        Assert.True(result.LocalDisconnected);
        Assert.Equal("remote_revoke_failed", result.SafeError);
        Assert.Null(await context.Store.GetAuthGrantForAccountAsync(setup.Account.AccountId));
        await setup.Worker.DisposeAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, StepOutcome.Rejected, "x_auth_or_scope_rejected")]
    [InlineData(HttpStatusCode.BadRequest, StepOutcome.Rejected, "x_post_rejected")]
    [InlineData(HttpStatusCode.InternalServerError, StepOutcome.Ambiguous, "x_publish_ambiguous_server_error")]
    public async Task Provider_errors_are_mapped_without_exposing_response_bodies(HttpStatusCode status, StepOutcome outcome, string safeError)
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(status, "{\"detail\":\"secret-provider-message\"}")));
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var input = Publication("text", setup.Account.AccountId);
        var step = await setup.Adapter.PlanNextStepAsync(input, null, default);
        var result = await setup.Adapter.ExecuteStepAsync(step, default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(safeError, result.SafeError);
        Assert.DoesNotContain("secret-provider-message", result.SafeError);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Rate_limit_uses_provider_reset_and_does_not_mark_dispatch_ambiguous()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("x-rate-limit-reset", context.Time.GetUtcNow().AddMinutes(3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var step = await setup.Adapter.PlanNextStepAsync(Publication("text", setup.Account.AccountId), null, default);
        var result = await setup.Adapter.ExecuteStepAsync(step, default);
        Assert.Equal(StepOutcome.Pending, result.Outcome);
        Assert.Equal(EffectCertainty.NoSideEffect, result.EffectCertainty);
        Assert.Equal(context.Time.GetUtcNow().AddMinutes(3), result.RetryAt);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Lost_or_malformed_success_response_is_unknown_and_never_reposted()
    {
        await using var context = await TestContext.CreateAsync();
        var calls = 0;
        using var http = Client((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json(HttpStatusCode.Created, "{\"data\":{}}"));
        });
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var queued = await setup.Posts.EnqueueAsync(XIntent(context, setup.Account.AccountId, "malformed", "only once"));
        await setup.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Unknown, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.RunOnceAsync();
        Assert.Equal(1, calls);
        Assert.Contains(await setup.Posts.QueueAsync(), item => item.Kind == JobKind.Reconcile);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Network_loss_after_dispatch_is_ambiguous_not_retryable_publish()
    {
        await using var context = await TestContext.CreateAsync();
        var calls = 0;
        using var http = Client((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("simulated disconnect");
        });
        var setup = await BuildAsync(context, http, "remote-user", "access", context.Time.GetUtcNow().AddHours(1));
        var queued = await setup.Posts.EnqueueAsync(XIntent(context, setup.Account.AccountId, "network-loss", "only once"));
        await setup.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Unknown, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.RunOnceAsync();
        Assert.Equal(1, calls);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Token_plaintext_is_not_stored_in_sqlite_or_returned_by_connection_status()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var secret = "unique-access-token-that-must-not-leak";
        var setup = await BuildAsync(context, http, "remote-user", secret, context.Time.GetUtcNow().AddHours(1));
        var statusJson = JsonSerializer.Serialize(await setup.Accounts.StatusAsync(setup.Account.AccountId));
        Assert.DoesNotContain(secret, statusJson);
        await setup.Worker.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var files = Directory.GetFiles(context.Directory, "post-router.db*");
        foreach (var file in files) Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
    }

    private static async Task<XSetup> BuildAsync(TestContext context, HttpClient http, string remoteSubject, string accessToken, DateTimeOffset expiresAt)
    {
        var client = new XApiClient(http, context.Time);
        var authProvider = new XAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store, new FileAuthGrantLockFactory(Path.Combine(context.Directory, "auth-locks")), context.Gate, [authProvider], context.Time);
        var accountLocks = new NoOpAccountOperationLockFactory();
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate, accountLocks, [authProvider], auth);
        var account = await SaveConnectionAsync(context, remoteSubject, accessToken, expiresAt);
        var adapter = new XProviderAdapter(auth, client, context.Time);
        var registry = new ProviderRegistry([adapter]);
        var posts = new PostService(context.Store, context.Gate, registry, context.Time);
        var worker = new WorkerService(context.Store, registry, new FileWorkerLockFactory(Path.Combine(context.Directory, "x-worker.lock")), context.Gate, context.Time, accountOperationLocks: accountLocks);
        return new(auth, accounts, account, adapter, posts, worker);
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context, string subject, string accessToken, DateTimeOffset expiresAt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial(accessToken, "refresh-token", expiresAt));
        var blob = await context.Store.PutAsync("auth-token", bytes);
        return await context.Store.SaveConnectedAccountAsync(new("x", "x-test", subject, "X Test", "client-id", XAuthProvider.RequiredScope, expiresAt, blob));
    }

    private static CanonicalPostIntent XIntent(TestContext context, Guid accountId, string key, string text) =>
        new(key, new Content(Guid.NewGuid(), ContentKind.TextOnly, text, null, []),
            [new TargetIntent(accountId, "x", "public", "x-options/v1", 1, "{}", "x-test")],
            new ScheduleIntent(ScheduleMode.Immediate, context.Time.GetUtcNow(), TimeSpan.FromMinutes(15)));

    private static ProviderPublication Publication(string text, Guid accountId)
    {
        var publication = new Publication(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), accountId, "x", PublicationState.Ready,
            ExecutionMode.Local, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15), DateTimeOffset.UtcNow);
        return new(publication, new Content(Guid.NewGuid(), ContentKind.TextOnly, text, null, []), new TargetIntent(accountId, "x", "public", "x-options/v1", 1, "{}"));
    }

    private static HttpClient Client(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> response)
    {
        var handler = new StubHandler(response);
        return new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
    }

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

    private sealed record XSetup(AuthCoordinator Auth, AccountConnectionService Accounts, AccountConnection Account,
        XProviderAdapter Adapter, PostService Posts, WorkerService Worker);
}
