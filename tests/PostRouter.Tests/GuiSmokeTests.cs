using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Gui;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class GuiSmokeTests
{
    [Fact]
    public async Task Gui_starts_and_serves_dashboard_create_list_detail_and_unknown_state()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        var ambiguous = await setup.Runtime.Posts.EnqueueAsync(FakeIntent(setup.FakeAccountId, "unknown-seed", "possibly sent"));
        setup.Runtime.FakeProvider.QueuePublish(new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: "response_lost"));
        _ = await setup.Runtime.Worker.RunOnceAsync();
        await setup.StartGuiAsync();
        Assert.Equal(IPAddress.Loopback.ToString(), setup.Client.BaseAddress!.Host);

        var html = await setup.Client.GetStringAsync("/");
        Assert.Contains("配信状況", html, StringComparison.Ordinal);
        Assert.Contains("投稿作成", html, StringComparison.Ordinal);
        Assert.Contains("アカウント", html, StringComparison.Ordinal);
        var script = await setup.Client.GetStringAsync("/app.js");
        Assert.Contains("disconnect", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revoke", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("自動再投稿", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);

        using var dashboard = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/dashboard"));
        Assert.Equal(1, dashboard.RootElement.GetProperty("unknown").GetInt32());
        var create = await setup.PostAsync("/api/posts", new
        {
            accountId = setup.FakeAccountId,
            text = "created in browser",
            clientRequestId = "gui-http-create",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var postId = created.RootElement.GetProperty("postId").GetGuid();
        using var scheduledResponse = await setup.PostAsync("/api/posts", new
        {
            accountId = setup.FakeAccountId,
            text = "scheduled in browser",
            clientRequestId = "gui-http-scheduled",
            publishAt = DateTimeOffset.UtcNow.AddHours(2),
        });
        Assert.Equal(HttpStatusCode.OK, scheduledResponse.StatusCode);

        using var list = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/publications"));
        Assert.Contains(list.RootElement.EnumerateArray(), item => item.GetProperty("postId").GetGuid() == postId);
        Assert.Contains(list.RootElement.EnumerateArray(), item => item.GetProperty("scheduleMode").GetString() == "AtTime");
        var unknownPublication = Assert.Single(ambiguous.PublicationIds);
        using var detail = JsonDocument.Parse(await setup.Client.GetStringAsync($"/api/publications/{unknownPublication:D}"));
        Assert.Equal("Unknown", detail.RootElement.GetProperty("summary").GetProperty("publicationState").GetString());
        Assert.True(detail.RootElement.GetProperty("canReconcile").GetBoolean());
        Assert.False(detail.RootElement.GetProperty("canRetry").GetBoolean());
        using var reconcile = await setup.PostAsync($"/api/publications/{unknownPublication:D}/reconcile", new { });
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        Assert.Contains("\"reposted\":false", await reconcile.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var cancel = await setup.PostAsync($"/api/posts/{postId:D}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    [Fact]
    public async Task Gui_rejects_unverified_mutations_and_returns_sanitized_validation_errors()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        using var missingCsrf = await setup.Client.PostAsJsonAsync("/api/posts", new { accountId = setup.FakeAccountId, text = "hello" });
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        using var invalid = await setup.PostAsync("/api/posts", new { accountId = setup.FakeAccountId, text = "", clientRequestId = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("invalid_input", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", body, StringComparison.Ordinal);

        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard");
        rebound.Headers.Host = "example.test";
        using var reboundResponse = await setup.Client.SendAsync(rebound);
        Assert.Equal(HttpStatusCode.BadRequest, reboundResponse.StatusCode);
    }

    [Fact]
    public async Task Gui_enqueues_jpeg_images_and_rejects_unexpected_media_without_path_disclosure()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        using var accepted = await setup.PostImagesAsync([("photo.jpg", "image/jpeg", new byte[] { 0xff, 0xd8, 0xff, 0x01 })], "image caption");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        using var acceptedJson = JsonDocument.Parse(acceptedBody);
        var postId = acceptedJson.RootElement.GetProperty("postId").GetGuid();
        Assert.DoesNotContain(setup.Directory, acceptedBody, StringComparison.OrdinalIgnoreCase);

        using var publications = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/publications"));
        Assert.Contains(publications.RootElement.EnumerateArray(), item => item.GetProperty("postId").GetGuid() == postId);

        using var rejected = await setup.PostImagesAsync([("fake.jpg", "image/jpeg", Encoding.ASCII.GetBytes("not-a-jpeg"))], "bad image");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("invalid_input", rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Directory, rejectedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(System.IO.Directory.EnumerateFiles(Path.Combine(setup.Directory, "incoming")));
    }

    [Fact]
    public async Task Account_api_never_returns_tokens_and_disconnect_preserves_queue()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        var material = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("access-secret-value", "refresh-secret-value", DateTimeOffset.UtcNow.AddHours(2)));
        var blob = await setup.Runtime.Store.PutAsync("auth-token", material);
        CryptographicOperations.ZeroMemory(material);
        var account = await setup.Runtime.Store.SaveConnectedAccountAsync(new(
            "x", "x-gui", "remote-user", "X GUI", "public-client", "tweet.read tweet.write users.read offline.access",
            DateTimeOffset.UtcNow.AddHours(2), blob));
        var queued = await setup.Runtime.Operations.EnqueueTextAsync(new(account.AccountId, "queued before disconnect", ClientRequestId: "disconnect-queue"));
        await setup.StartGuiAsync();

        var accountsJson = await setup.Client.GetStringAsync("/api/accounts");
        Assert.DoesNotContain("access-secret-value", accountsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret-value", accountsJson, StringComparison.Ordinal);
        using var disconnect = await setup.PostAsync($"/api/accounts/{account.AccountId:D}/disconnect", new { });
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        Assert.Contains("Disconnected", await disconnect.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(await setup.Runtime.Posts.QueueAsync(), item => item.OwnerId == Assert.Single(queued.PublicationIds));
        Assert.Equal(0, await setup.Runtime.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Connect_starts_shared_pkce_flow_without_returning_verifier_or_tokens()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        using var response = await setup.PostAsync("/api/accounts/connect", new { provider = "x", clientId = "public-client", alias = "x-main" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("https://x.com/i/oauth2/authorize", body, StringComparison.Ordinal);
        Assert.Contains("code_challenge", body, StringComparison.Ordinal);
        Assert.DoesNotContain("codeVerifier", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OAuth_callback_with_unknown_state_does_not_echo_authorization_code()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();

        using var response = await setup.Client.GetAsync("/oauth/callback?state=unknown&code=authorization-code-secret");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<code>connection_failed</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-code-secret", html, StringComparison.Ordinal);
    }

    private static CanonicalPostIntent FakeIntent(Guid accountId, string key, string text) => new(
        key, new Content(Guid.NewGuid(), ContentKind.TextOnly, text, null, []),
        [new TargetIntent(accountId, "fake", "public", "fake-options/v1", 1, "{}", "fake-gui")],
        new ScheduleIntent(ScheduleMode.Immediate, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)));

    private sealed class GuiTestSetup : IAsyncDisposable
    {
        private readonly string? _previousProfile;
        private readonly string? _previousKey;
        private GuiHost? _host;

        private GuiTestSetup(string directory, PostRouterRuntime runtime, string? previousProfile, string? previousKey)
        {
            Directory = directory;
            Runtime = runtime;
            _previousProfile = previousProfile;
            _previousKey = previousKey;
            Client = new HttpClient();
        }

        public string Directory { get; }
        public PostRouterRuntime Runtime { get; }
        public HttpClient Client { get; }
        public Guid FakeAccountId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
        public string CsrfToken { get; private set; } = string.Empty;

        public static async Task<GuiTestSetup> CreateAsync()
        {
            var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
            var previousKey = Environment.GetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY");
            var key = new byte[32];
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
            Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", Convert.ToBase64String(key));
            var directory = Path.Combine(Path.GetTempPath(), "post-router-gui-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var runtime = await RuntimeFactory.CreateAsync(directory, new InMemoryMasterKeyStore(key));
            _ = await runtime.Posts.EnqueueAsync(new CanonicalPostIntent(
                "fake-account-seed", new Content(Guid.NewGuid(), ContentKind.TextOnly, "seed", null, []),
                [new TargetIntent(Guid.Parse("99999999-9999-9999-9999-999999999999"), "fake", "public", "fake-options/v1", 1, "{}", "fake-gui")],
                new ScheduleIntent(ScheduleMode.AtTime, DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromMinutes(15))));
            return new(directory, runtime, previousProfile, previousKey);
        }

        public async Task StartGuiAsync()
        {
            _host = await GuiApplication.StartAsync(new GuiOptions(Directory, 0, false));
            Client.BaseAddress = _host.Address;
            using var session = JsonDocument.Parse(await Client.GetStringAsync("/api/session"));
            CsrfToken = session.RootElement.GetProperty("csrfToken").GetString()!;
        }

        public async Task<HttpResponseMessage> PostAsync(string path, object value)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(value),
            };
            request.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-Post-Router-CSRF", CsrfToken);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostImagesAsync(
            IReadOnlyList<(string Name, string ContentType, byte[] Bytes)> images, string text)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(FakeAccountId.ToString("D")), "accountId");
            content.Add(new StringContent(text), "text");
            content.Add(new StringContent($"gui-image-{Guid.NewGuid():N}"), "clientRequestId");
            foreach (var image in images)
            {
                var bytes = new ByteArrayContent(image.Bytes);
                bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
                content.Add(bytes, "images", image.Name);
            }
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/posts/images") { Content = content };
            request.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-Post-Router-CSRF", CsrfToken);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (_host is not null) await _host.DisposeAsync();
            await Runtime.DisposeAsync();
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", _previousProfile);
            Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", _previousKey);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }
}
