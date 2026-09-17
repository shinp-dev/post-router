using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Gui;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class GuiSmokeTests
{
    [Fact]
    public async Task Media_gui_stages_recovers_and_deletes_through_http_without_duplicate_upload()
    {
        using var github = new FakeGitHubMediaServer();
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync(_ => Task.FromResult(new GitHubReleaseMediaHost(
            new GitHubReleaseMediaHostOptions("example", "media", "staging"),
            new FakeGitHubMediaTokens(), github.Client, github.Client, TimeProvider.System)));
        using var configured = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "staging"
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        using var staleForm = new MultipartFormDataContent();
        var staleImage = new ByteArrayContent([0xff, 0xd8, 0xff, 1]);
        staleImage.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        staleForm.Add(staleImage, "media", "private-original.jpg");
        staleForm.Add(new StringContent("true"), "acknowledgePublic");
        staleForm.Add(new StringContent("example"), "expectedOwner");
        staleForm.Add(new StringContent("media"), "expectedRepository");
        staleForm.Add(new StringContent("different-tag"), "expectedReleaseTag");
        using var staleResponse = await setup.PostFormAsync("/api/media/stage", staleForm);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Equal(0, github.UploadCalls);

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent([0xff, 0xd8, 0xff, 1, 2, 3, 4]);
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "media", "private-original.jpg");
        form.Add(new StringContent("true"), "acknowledgePublic");
        form.Add(new StringContent("example"), "expectedOwner");
        form.Add(new StringContent("media"), "expectedRepository");
        form.Add(new StringContent("staging"), "expectedReleaseTag");
        using var stagedResponse = await setup.PostFormAsync("/api/media/stage", form);
        Assert.Equal(HttpStatusCode.OK, stagedResponse.StatusCode);
        var stagedBody = await stagedResponse.Content.ReadAsStringAsync();
        using var stagedJson = JsonDocument.Parse(stagedBody);
        Assert.Equal("Pending", stagedJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("github_upload_outcome_unknown", stagedJson.RootElement.GetProperty("lastErrorCode").GetString());
        Assert.DoesNotContain("private-original", stagedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Directory, stagedBody, StringComparison.OrdinalIgnoreCase);
        var id = stagedJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1, github.UploadCalls);
        using var blockedChange = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "other-tag"
        });
        Assert.Equal(HttpStatusCode.Conflict, blockedChange.StatusCode);

        github.MakeAssetAvailable();
        using var recoveredResponse = await setup.PostAsync($"/api/media/operations/{id:D}/recover", new { });
        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
        var recoveredBody = await recoveredResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Staged\"", recoveredBody, StringComparison.Ordinal);
        Assert.Contains("https://github.com/example/media/releases/download/staging/", recoveredBody, StringComparison.Ordinal);
        Assert.Equal(1, github.UploadCalls);

        using var unconfirmed = await setup.PostAsync($"/api/media/operations/{id:D}/delete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal(0, github.DeleteCalls);
        using var deletedResponse = await setup.PostAsync($"/api/media/operations/{id:D}/delete", new { confirmed = true });
        Assert.Equal(HttpStatusCode.OK, deletedResponse.StatusCode);
        Assert.Contains("\"status\":\"Deleted\"", await deletedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, github.DeleteCalls);
        using var allowedChange = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "other-tag"
        });
        Assert.Equal(HttpStatusCode.OK, allowedChange.StatusCode);
        using var history = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/media/operations"));
        Assert.Equal("Deleted", Assert.Single(history.RootElement.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task GitHub_media_settings_keep_pat_in_vault_and_never_return_it_to_browser()
    {
        const string pat = "github-test-pat-secret-marker";
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        var html = await setup.Client.GetStringAsync("/");
        var script = await setup.Client.GetStringAsync("/app.js");
        Assert.Contains("GitHub一時公開メディア", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);

        using var initial = await setup.Client.GetAsync("/api/media/settings");
        Assert.Contains("\"credentialConfigured\":false", await initial.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var csrfRejected = await setup.Client.PostAsJsonAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "staging"
        });
        Assert.Equal(HttpStatusCode.Forbidden, csrfRejected.StatusCode);

        using var configured = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "staging"
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var invalidCredential = await setup.PostAsync("/api/media/credential", new { token = pat + " invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCredential.StatusCode);
        Assert.DoesNotContain(pat, await invalidCredential.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var registered = await setup.PostAsync("/api/media/credential", new { token = pat });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.DoesNotContain(pat, await registered.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var status = await setup.Client.GetStringAsync("/api/media/settings");
        Assert.Contains("\"credentialConfigured\":true", status, StringComparison.Ordinal);
        Assert.DoesNotContain(pat, status, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialBlobId", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pat, await File.ReadAllTextAsync(Path.Combine(setup.Directory, "github-media-settings.json")), StringComparison.Ordinal);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(setup.Directory, "post-router.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ciphertext FROM vault_blobs WHERE purpose='github-media-staging-token'";
            var ciphertext = (byte[])(await command.ExecuteScalarAsync())!;
            Assert.False(ciphertext.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(pat)));
        }

        using var retagged = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "media",
            releaseTag = "staging-2"
        });
        Assert.Contains("\"credentialConfigured\":true", await retagged.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var changed = await setup.PostAsync("/api/media/settings", new
        {
            owner = "example",
            repository = "other",
            releaseTag = "staging"
        });
        Assert.Contains("\"credentialConfigured\":false", await changed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_gui_exposes_staging_controls_and_requires_csrf_for_mutations()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        var html = await setup.Client.GetStringAsync("/");
        var script = await setup.Client.GetStringAsync("/app.js");
        Assert.Contains("media-stage-acknowledge", html, StringComparison.Ordinal);
        Assert.Contains("recover（照会のみ）", script, StringComparison.Ordinal);
        Assert.Contains("GitHub上のassetを削除", script, StringComparison.Ordinal);
        using var list = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/media/operations"));
        Assert.Empty(list.RootElement.EnumerateArray());
        using var unverified = await setup.Client.PostAsJsonAsync($"/api/media/operations/{Guid.NewGuid():D}/delete", new { });
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
    }

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
        using var reconnect = await setup.PostAsync($"/api/accounts/{account.AccountId:D}/reconnect", new { });
        Assert.Equal(HttpStatusCode.OK, reconnect.StatusCode);
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
    public async Task YouTube_connect_accepts_optional_secret_without_exposing_it_in_browser_response()
    {
        const string clientSecret = "gui-client-secret-marker";
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();

        using var response = await setup.PostAsync("/api/accounts/connect", new
        {
            provider = "youtube",
            clientId = "desktop-client",
            alias = "youtube-main",
            clientSecret,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("code_challenge_method=S256", body, StringComparison.Ordinal);
        Assert.DoesNotContain(clientSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        var html = await setup.Client.GetStringAsync("/");
        Assert.Contains("id=\"client-secret-file\" type=\"file\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(clientSecret, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_reconnect_accepts_secret_after_local_credentials_are_removed()
    {
        const string clientSecret = "reconnect-client-secret-marker";
        await using var setup = await GuiTestSetup.CreateAsync();
        var tokenBytes = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), clientSecret));
        var blobId = await setup.Runtime.Store.PutAsync("auth-token", tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var account = await setup.Runtime.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-main", "channel-123", "Channel Name", "desktop-client",
            "https://www.googleapis.com/auth/youtube.force-ssl", DateTimeOffset.UtcNow.AddHours(1), blobId));
        Assert.True(await setup.Runtime.Accounts.DisconnectAsync(account.AccountId));
        await setup.StartGuiAsync();

        using var response = await setup.PostAsync($"/api/accounts/{account.AccountId:D}/reconnect", new { clientSecret });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("authorizationUrl", body, StringComparison.Ordinal);
        Assert.DoesNotContain(clientSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        var html = await setup.Client.GetStringAsync("/");
        Assert.Contains("id=\"reconnect-client-secret-file\" type=\"file\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_secret_file_starts_oauth_without_echoing_secret_or_raw_file_errors()
    {
        const string secret = "gui-secret-file-marker";
        await using var setup = await GuiTestSetup.CreateAsync();
        await setup.StartGuiAsync();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("youtube"), "provider");
        content.Add(new StringContent("desktop-client"), "clientId");
        content.Add(new StringContent("youtube-main"), "alias");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(secret + "\n")), "clientSecretFile", "secret.txt");
        using var response = await setup.PostFormAsync("/api/accounts/connect/file", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("code_challenge_method=S256", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, await setup.Client.GetStringAsync("/"), StringComparison.Ordinal);

        using var bad = new MultipartFormDataContent();
        bad.Add(new StringContent("youtube"), "provider");
        bad.Add(new StringContent("desktop-client"), "clientId");
        bad.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(new string('s', 4097))), "clientSecretFile", "secret.txt");
        using var rejected = await setup.PostFormAsync("/api/accounts/connect/file", bad);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("invalid_input", error, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 100), error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_video_form_enqueues_private_mp4_with_canonical_options()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        var blob = await setup.Runtime.Store.PutAsync("auth-token", JsonSerializer.SerializeToUtf8Bytes(
            new TokenMaterial("access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));
        var account = await setup.Runtime.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-main", "channel-123", "Channel Name", "desktop-client",
            "https://www.googleapis.com/auth/youtube.force-ssl", DateTimeOffset.UtcNow.AddHours(1), blob));
        await setup.StartGuiAsync();

        using var accounts = JsonDocument.Parse(await setup.Client.GetStringAsync("/api/accounts"));
        Assert.Contains(accounts.RootElement.EnumerateArray(), item =>
            item.GetProperty("accountId").GetGuid() == account.AccountId && item.GetProperty("status").GetString() == "Connected");
        var html = await setup.Client.GetStringAsync("/");
        var script = await setup.Client.GetStringAsync("/app.js");
        Assert.Contains("id=\"post-video\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"post-title\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"post-made-for-kids\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"post-upload-notice\"", html, StringComparison.Ordinal);
        Assert.Contains("contentKinds.some", script, StringComparison.Ordinal);
        Assert.Contains("contentKinds.includes(\"TextOnly\")", script, StringComparison.Ordinal);
        Assert.Contains("/api/posts/video", script, StringComparison.Ordinal);
        Assert.Contains("/api/posts/images", script, StringComparison.Ordinal);

        using var accepted = await PostVideoAsync(setup, account.AccountId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var postId = acceptedJson.RootElement.GetProperty("postId").GetGuid();
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(setup.Directory, "post-router.db")}");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
SELECT content.kind,content.text,content.title,target.visibility,target.options_schema,target.options_json
FROM posts post JOIN contents content ON content.id=post.content_id
JOIN targets target ON target.post_id=post.id WHERE post.id=$id
""";
        command.Parameters.AddWithValue("$id", postId.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("Video", row.GetString(0));
        Assert.Equal("video description", row.GetString(1));
        Assert.Equal("GUI video title", row.GetString(2));
        Assert.Equal("private", row.GetString(3));
        Assert.Equal("youtube-options/v1", row.GetString(4));
        using var options = JsonDocument.Parse(row.GetString(5));
        Assert.False(options.RootElement.GetProperty("madeForKids").GetBoolean());
        Assert.True(options.RootElement.GetProperty("containsSyntheticMedia").GetBoolean());
        Assert.True(options.RootElement.GetProperty("uploadNoticeAcknowledged").GetBoolean());
    }

    [Theory]
    [InlineData(false, "GUI video title", "false", true)]
    [InlineData(true, "", "false", true)]
    [InlineData(true, "GUI video title", null, true)]
    [InlineData(true, "GUI video title", "false", false)]
    public async Task YouTube_video_form_requires_mp4_title_kids_and_upload_notice(
        bool includeVideo, string title, string? madeForKids, bool acknowledged)
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        var blob = await setup.Runtime.Store.PutAsync("auth-token", JsonSerializer.SerializeToUtf8Bytes(
            new TokenMaterial("access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));
        var account = await setup.Runtime.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-main", "channel-123", "Channel Name", "desktop-client",
            "https://www.googleapis.com/auth/youtube.force-ssl", DateTimeOffset.UtcNow.AddHours(1), blob));
        await setup.StartGuiAsync();
        using var response = await PostVideoAsync(setup, account.AccountId, includeVideo, title, madeForKids, acknowledged);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_input", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_video_title_uses_provider_unicode_scalar_validation()
    {
        await using var setup = await GuiTestSetup.CreateAsync();
        var blob = await setup.Runtime.Store.PutAsync("auth-token", JsonSerializer.SerializeToUtf8Bytes(
            new TokenMaterial("access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));
        var account = await setup.Runtime.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-main", "channel-123", "Channel Name", "desktop-client",
            "https://www.googleapis.com/auth/youtube.force-ssl", DateTimeOffset.UtcNow.AddHours(1), blob));
        await setup.StartGuiAsync();
        using var accepted = await PostVideoAsync(setup, account.AccountId, title: string.Concat(Enumerable.Repeat("😀", 100)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var rejected = await PostVideoAsync(setup, account.AccountId, title: string.Concat(Enumerable.Repeat("😀", 101)));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostVideoAsync(GuiTestSetup setup, Guid accountId,
        bool includeVideo = true, string title = "GUI video title", string? madeForKids = "false", bool acknowledged = true)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(accountId.ToString("D")), "accountId");
        content.Add(new StringContent(title), "title");
        content.Add(new StringContent("video description"), "description");
        if (madeForKids is not null) content.Add(new StringContent(madeForKids), "madeForKids");
        content.Add(new StringContent("true"), "containsSyntheticMedia");
        content.Add(new StringContent(acknowledged ? "true" : "false"), "uploadNoticeAcknowledged");
        content.Add(new StringContent("private"), "visibility");
        content.Add(new StringContent($"gui-video-{Guid.NewGuid():N}"), "clientRequestId");
        if (includeVideo) content.Add(new ByteArrayContent([0, 0, 0, 0, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0]),
            "video", "test.mp4");
        return await setup.PostFormAsync("/api/posts/video", content);
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

        public async Task StartGuiAsync(Func<CancellationToken, Task<GitHubReleaseMediaHost>>? createMediaHost = null)
        {
            _host = createMediaHost is null
                ? await GuiApplication.StartAsync(new GuiOptions(Directory, 0, false))
                : await GuiApplication.StartForTestsAsync(new GuiOptions(Directory, 0, false), createMediaHost);
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

        public async Task<HttpResponseMessage> PostFormAsync(string path, MultipartFormDataContent content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
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

    private sealed class FakeGitHubMediaTokens : IGitHubMediaTokenSource
    {
        public Task<byte[]> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.ASCII.GetBytes("fake-media-token"));
    }

    private sealed class FakeGitHubMediaServer : HttpMessageHandler
    {
        private string _assetName = "";
        private long _assetSize;
        private bool _available;
        public HttpClient Client { get; }
        public int UploadCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public FakeGitHubMediaServer() => Client = new HttpClient(this, disposeHandler: false);

        public void MakeAssetAvailable() => _available = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fake-media-token", request.Headers.Authorization?.Parameter);
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/example/media")
                return Json(HttpStatusCode.OK, "{\"full_name\":\"example/media\",\"private\":false}");
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/example/media/releases/tags/staging")
                return Json(HttpStatusCode.OK, "{\"id\":42,\"tag_name\":\"staging\",\"draft\":false}");
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/example/media/releases/42/assets")
                return Json(HttpStatusCode.OK, _available ? $"[{AssetJson()}]" : "[]");
            if (uri.Host == "uploads.github.com" && request.Method == HttpMethod.Post)
            {
                UploadCalls++;
                _assetName = Uri.UnescapeDataString(uri.Query["?name=".Length..]);
                _assetSize = (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).LongLength;
                throw new HttpRequestException("fake-media-token private diagnostic");
            }
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/example/media/releases/assets/555")
            {
                if (request.Method == HttpMethod.Get)
                    return _available ? Json(HttpStatusCode.OK, AssetJson()) : Json(HttpStatusCode.NotFound, "{}");
                if (request.Method == HttpMethod.Delete)
                {
                    DeleteCalls++;
                    _available = false;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }
            throw new InvalidOperationException("Unexpected fake GitHub request.");
        }

        private string AssetJson() => JsonSerializer.Serialize(new
        {
            id = 555,
            name = _assetName,
            state = "uploaded",
            size = _assetSize,
            browser_download_url = $"https://github.com/example/media/releases/download/staging/{_assetName}"
        });

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) Client.Dispose();
            base.Dispose(disposing);
        }
    }
}
