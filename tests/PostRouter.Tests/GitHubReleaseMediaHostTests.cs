using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class GitHubReleaseMediaHostTests
{
    [Fact]
    public async Task Connection_check_reads_release_without_uploading_media()
    {
        using var setup = new Setup();
        await setup.Host.CheckConnectionAsync();
        Assert.Equal(0, setup.Server.UploadCalls);
        Assert.Equal(2, setup.Server.Hosts.Count);
        Assert.All(setup.Server.Hosts, host => Assert.Equal("api.github.com", host));
        Assert.True(setup.Server.AllAuthenticated);
    }

    [Fact]
    public async Task Connection_check_rejects_private_repository_without_upload()
    {
        using var setup = new Setup();
        setup.Server.PrivateRepository = true;
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.CheckConnectionAsync());
        Assert.Equal("github_repository_not_public", error.Code);
        Assert.Equal(0, setup.Server.UploadCalls);
    }

    [Theory]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("image/jpeg", ".jpg")]
    public async Task Stages_verified_media_with_opaque_name_and_source_receipt(string mime, string extension)
    {
        using var setup = new Setup();
        var asset = setup.Asset(mime);
        var operation = setup.Host.Prepare(asset);
        var staged = await setup.Host.StageAsync(asset, operation);

        Assert.Equal($"https://github.com/example/media/releases/download/staging/{setup.Server.AssetName}", staged.PublicUrl.AbsoluteUri);
        Assert.EndsWith(extension, setup.Server.AssetName, StringComparison.Ordinal);
        Assert.DoesNotContain("original-private", setup.Server.AssetName, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(asset.StorageRef), staged.PublicUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(asset.Sha256, staged.SourceSha256);
        Assert.Equal(asset.SizeBytes, staged.SourceSizeBytes);
        Assert.Equal(setup.Bytes, setup.Server.UploadedBytes);
        Assert.True(setup.Server.AllAuthenticated);
        Assert.Equal(1, setup.Server.UploadCalls);
        Assert.Equal(2, setup.Server.Hosts.Distinct().Count());
        Assert.Contains("api.github.com", setup.Server.Hosts);
        Assert.Contains("uploads.github.com", setup.Server.Hosts);
        Assert.DoesNotContain("fixture-token", staged.PublicUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Rejects_sha256_mismatch_before_any_request()
    {
        using var setup = new Setup();
        var asset = setup.Asset("video/mp4") with { Sha256 = new string('a', 64) };
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("media_sha256_mismatch", error.Code);
        Assert.Empty(setup.Server.Hosts);
    }

    [Fact]
    public async Task Rejects_size_mismatch_before_any_request()
    {
        using var setup = new Setup();
        var asset = setup.Asset("image/jpeg") with { SizeBytes = 1234 };
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("media_size_mismatch", error.Code);
        Assert.Empty(setup.Server.Hosts);
    }

    [Fact]
    public void Rejects_oversized_asset_before_persisting_a_stage_operation()
    {
        using var setup = new Setup();
        var asset = setup.Asset("image/jpeg") with { SizeBytes = 2L * 1024 * 1024 * 1024 + 1 };
        var error = Assert.Throws<TemporaryPublicMediaException>(() => setup.Host.Prepare(asset));
        Assert.Equal("media_size_limit_exceeded", error.Code);
        Assert.Empty(setup.Server.Hosts);
    }

    [Fact]
    public async Task Missing_media_is_safe_and_never_uploaded()
    {
        using var setup = new Setup();
        var asset = setup.Asset("video/mp4");
        File.Delete(asset.StorageRef);
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("media_missing", error.Code);
        Assert.DoesNotContain(asset.StorageRef, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(setup.Server.Hosts);
    }

    [Theory]
    [InlineData("not json", "github_invalid_response")]
    [InlineData("{\"name\":\"unused\",\"state\":\"uploaded\"}", "github_invalid_response")]
    public async Task Rejects_malformed_upload_response(string body, string expectedCode)
    {
        using var setup = new Setup();
        setup.Server.UploadBody = body;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Rejects_missing_asset_id()
    {
        using var setup = new Setup();
        setup.Server.UploadBodyFactory = server => JsonSerializer.Serialize(new
        {
            name = server.AssetName,
            state = "uploaded",
            size = setup.Bytes.Length,
            browser_download_url = server.BrowserUrl,
        });
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_invalid_response", error.Code);
    }

    [Fact]
    public async Task Rejects_missing_browser_download_url()
    {
        using var setup = new Setup();
        setup.Server.UploadBodyFactory = server => JsonSerializer.Serialize(new
        {
            id = 555,
            name = server.AssetName,
            state = "uploaded",
            size = setup.Bytes.Length,
        });
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_invalid_response", error.Code);
    }

    [Theory]
    [InlineData("http://github.com/example/media/releases/download/staging/file.mp4")]
    [InlineData("https://evil.example/steal")]
    public async Task Rejects_unsafe_public_url(string url)
    {
        using var setup = new Setup();
        setup.Server.BrowserUrlOverride = url;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_public_url_invalid", error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, "github_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, false, "github_forbidden")]
    [InlineData(HttpStatusCode.Forbidden, true, "github_rate_limited")]
    [InlineData(HttpStatusCode.TooManyRequests, false, "github_rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, false, "github_server_error")]
    public async Task Maps_api_failures_to_safe_codes(HttpStatusCode status, bool rateLimit, string expectedCode)
    {
        using var setup = new Setup();
        setup.Server.ReleaseError = status;
        setup.Server.RateLimit = rateLimit;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, setup.Server.UploadCalls);
        Assert.DoesNotContain("fixture-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-body", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, "github_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, false, "github_forbidden")]
    [InlineData(HttpStatusCode.Forbidden, true, "github_rate_limited")]
    public async Task Upload_auth_failures_are_safe_and_not_retried(HttpStatusCode status, bool rateLimit, string expectedCode)
    {
        using var setup = new Setup();
        setup.Server.UploadError = status;
        setup.Server.RateLimit = rateLimit;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(1, setup.Server.UploadCalls);
        Assert.DoesNotContain("fixture-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-body", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_asset_list_fails_closed_before_upload()
    {
        using var setup = new Setup();
        setup.Server.ListBody = "[{}]";
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_invalid_response", error.Code);
        Assert.Equal(0, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Network_diagnostic_containing_token_is_not_exposed()
    {
        using var setup = new Setup();
        setup.Server.ReleaseThrowsDiagnostic = true;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_network_error", error.Code);
        Assert.DoesNotContain("fixture-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-body", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_before_upload_response_does_not_retry()
    {
        using var setup = new Setup();
        setup.Server.TimeoutBeforeStore = true;
        var asset = setup.Asset("video/mp4");
        var operation = setup.Host.Prepare(asset);
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, operation));
        Assert.Equal("github_upload_outcome_unknown", error.Code);
        Assert.Equal(1, setup.Server.UploadCalls);
        Assert.Null(await setup.Host.RecoverAsync(operation));
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Lost_upload_response_is_recovered_by_operation_bound_asset_list()
    {
        using var setup = new Setup();
        setup.Server.TimeoutAfterStore = true;
        var asset = setup.Asset("video/mp4");
        var operation = setup.Host.Prepare(asset);
        var staged = await setup.Host.StageAsync(asset, operation);
        Assert.Equal(1, setup.Server.UploadCalls);
        var restoredOperation = JsonSerializer.Deserialize<PublicMediaStagingOperation>(JsonSerializer.Serialize(operation))!;
        using var afterRestart = setup.NewHost();
        Assert.Equal(staged.PublicUrl, (await afterRestart.RecoverAsync(restoredOperation))?.PublicUrl);
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Caller_can_set_logical_expiry_without_automatic_deletion()
    {
        using var setup = new Setup();
        var asset = setup.Asset("image/jpeg");
        var expiry = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var staged = await setup.Host.StageAsync(asset, setup.Host.Prepare(asset, expiry));
        Assert.Equal(expiry, staged.ExpiresAt);
        Assert.True(setup.Server.HasAsset);
    }

    [Fact]
    public async Task Failed_upload_does_not_repeat_and_can_be_recovered_later()
    {
        using var setup = new Setup();
        setup.Server.UploadError = HttpStatusCode.BadGateway;
        var asset = setup.Asset("video/mp4");
        var operation = setup.Host.Prepare(asset);
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, operation));
        Assert.Equal("github_upload_outcome_unknown", error.Code);
        Assert.Equal(1, setup.Server.UploadCalls);
        setup.Server.HasAsset = true;
        Assert.NotNull(await setup.Host.RecoverAsync(operation));
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Deletes_from_serialized_handle_after_restart_and_treats_missing_asset_as_deleted()
    {
        using var setup = new Setup();
        var asset = setup.Asset("image/jpeg");
        var staged = await setup.Host.StageAsync(asset, setup.Host.Prepare(asset));
        var json = JsonSerializer.Serialize(staged.Handle);
        Assert.DoesNotContain("fixture-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain(asset.StorageRef, json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<StagedPublicAssetHandle>(json)!;
        using (var payload = JsonDocument.Parse(Convert.FromBase64String(restored.Value)))
        {
            var fields = payload.RootElement;
            Assert.Equal("github-release-asset", fields.GetProperty("Host").GetString());
            Assert.Equal(1, fields.GetProperty("Version").GetInt32());
            Assert.Equal("example", fields.GetProperty("Owner").GetString());
            Assert.Equal("media", fields.GetProperty("Repository").GetString());
            Assert.Equal("staging", fields.GetProperty("Tag").GetString());
            Assert.Equal(42, fields.GetProperty("ReleaseId").GetInt64());
            Assert.Equal(555, fields.GetProperty("AssetId").GetInt64());
            Assert.Equal(setup.Server.AssetName, fields.GetProperty("AssetName").GetString());
            Assert.Equal(staged.PublicUrl.AbsoluteUri, fields.GetProperty("PublicUrl").GetString());
            Assert.Equal(asset.Sha256, fields.GetProperty("SourceSha256").GetString());
            Assert.Equal(asset.SizeBytes, fields.GetProperty("SourceSizeBytes").GetInt64());
            Assert.True(fields.TryGetProperty("CreatedAt", out _));
            Assert.True(fields.TryGetProperty("ExpiresAt", out _));
            Assert.DoesNotContain("fixture-token", fields.ToString(), StringComparison.Ordinal);
        }
        using var afterRestart = setup.NewHost();
        await afterRestart.DeleteAsync(restored);
        Assert.Equal(1, setup.Server.DeleteCalls);
        Assert.False(setup.Server.HasAsset);
        await afterRestart.DeleteAsync(restored);
        Assert.Equal(1, setup.Server.DeleteCalls);
        Assert.Equal("[OPAQUE STAGED MEDIA HANDLE]", restored.ToString());
    }

    [Fact]
    public async Task Delete_failure_keeps_safe_error_and_does_not_delete_release()
    {
        using var setup = new Setup();
        var asset = setup.Asset("video/mp4");
        var staged = await setup.Host.StageAsync(asset, setup.Host.Prepare(asset));
        setup.Server.DeleteError = HttpStatusCode.Forbidden;
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.DeleteAsync(staged.Handle));
        Assert.Equal("github_forbidden", error.Code);
        Assert.True(setup.Server.HasAsset);
        Assert.All(setup.Server.Methods.Where(value => value.Method == HttpMethod.Delete),
            value => Assert.Contains("/releases/assets/", value.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reusing_same_operation_finds_existing_asset_without_second_upload()
    {
        using var setup = new Setup();
        var asset = setup.Asset("video/mp4");
        var operation = setup.Host.Prepare(asset);
        var first = await setup.Host.StageAsync(asset, operation);
        var second = await setup.Host.StageAsync(asset, operation);
        Assert.Equal(first.PublicUrl, second.PublicUrl);
        Assert.Equal(1, setup.Server.UploadCalls);
    }

    [Fact]
    public async Task Redirect_is_rejected_without_following_another_host()
    {
        using var setup = new Setup();
        setup.Server.ReleaseError = HttpStatusCode.Redirect;
        var asset = setup.Asset("video/mp4");
        var error = await Assert.ThrowsAsync<TemporaryPublicMediaException>(() => setup.Host.StageAsync(asset, setup.Host.Prepare(asset)));
        Assert.Equal("github_redirect_rejected", error.Code);
        Assert.Single(setup.Server.Hosts);
        Assert.Equal("api.github.com", setup.Server.Hosts[0]);
    }

    [Fact]
    public async Task Vault_token_source_reads_existing_encrypted_vault_boundary()
    {
        await using var context = await TestContext.CreateAsync();
        var blobId = await context.Store.PutAsync(VaultGitHubMediaTokenSource.Purpose, Encoding.ASCII.GetBytes("fixture-token"));
        var source = new VaultGitHubMediaTokenSource(context.Store, blobId);
        var bytes = await source.GetTokenAsync();
        try { Assert.Equal("fixture-token", Encoding.ASCII.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        await using var database = new SqliteConnection($"Data Source={Path.Combine(context.Directory, "post-router.db")}");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT ciphertext FROM vault_blobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", blobId);
        var ciphertext = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain("fixture-token", Encoding.ASCII.GetString(ciphertext), StringComparison.Ordinal);
    }

    private sealed class Setup : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "post-router-media-host-tests", Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        public Setup()
        {
            Directory.CreateDirectory(_directory);
            Server = new FakeGitHub();
            _http = new HttpClient(Server);
            Host = NewHost();
        }
        public FakeGitHub Server { get; }
        public GitHubReleaseMediaHost Host { get; }
        public byte[] Bytes { get; private set; } = [];

        public GitHubReleaseMediaHost NewHost() => new(
            new GitHubReleaseMediaHostOptions("example", "media", "staging"),
            new FakeTokens(), _http, _http,
            new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero)));

        public MediaAsset Asset(string mime)
        {
            Bytes = mime == "video/mp4"
                ? [0, 0, 0, 12, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4]
                : [0xff, 0xd8, 0xff, 1, 2, 3, 4];
            var path = Path.Combine(_directory, mime == "video/mp4" ? "original-private.mp4" : "original-private.jpg");
            File.WriteAllBytes(path, Bytes);
            return new MediaAsset(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Bytes)), Bytes.Length, mime, path);
        }

        public void Dispose()
        {
            Host.Dispose();
            _http.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakeTokens : IGitHubMediaTokenSource
    {
        public Task<byte[]> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.ASCII.GetBytes("fixture-token"));
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        public List<(HttpMethod Method, string Path)> Methods { get; } = [];
        public bool AllAuthenticated { get; private set; } = true;
        public int UploadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string AssetName { get; private set; } = "";
        public byte[] UploadedBytes { get; private set; } = [];
        public bool HasAsset { get; set; }
        public bool TimeoutBeforeStore { get; set; }
        public bool TimeoutAfterStore { get; set; }
        public bool RateLimit { get; set; }
        public bool PrivateRepository { get; set; }
        public bool ReleaseThrowsDiagnostic { get; set; }
        public HttpStatusCode? ReleaseError { get; set; }
        public HttpStatusCode? UploadError { get; set; }
        public HttpStatusCode? DeleteError { get; set; }
        public string? ListBody { get; set; }
        public string? UploadBody { get; set; }
        public Func<FakeGitHub, string>? UploadBodyFactory { get; set; }
        public string? BrowserUrlOverride { get; set; }
        public string BrowserUrl => BrowserUrlOverride ??
            $"https://github.com/example/media/releases/download/staging/{AssetName}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            Methods.Add((request.Method, uri.AbsolutePath));
            AllAuthenticated &= request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == "fixture-token";
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/example/media")
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    full_name = "example/media",
                    @private = PrivateRepository
                }));
            if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/releases/tags/staging", StringComparison.Ordinal))
            {
                if (ReleaseThrowsDiagnostic) throw new HttpRequestException("fixture-token secret-provider-body");
                if (ReleaseError is { } status)
                {
                    var response = Json(status, "{\"message\":\"secret-provider-body\"}");
                    if (RateLimit) response.Headers.Add("X-RateLimit-Remaining", "0");
                    if (status == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://evil.example/steal");
                    return response;
                }
                return Json(HttpStatusCode.OK, "{\"id\":42,\"tag_name\":\"staging\",\"draft\":false}");
            }
            if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/releases/42/assets", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ListBody ?? (HasAsset ? $"[{AssetJson()}]" : "[]"));
            if (uri.Host == "uploads.github.com" && request.Method == HttpMethod.Post)
            {
                UploadCalls++;
                AssetName = Uri.UnescapeDataString(uri.Query["?name=".Length..]);
                UploadedBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (TimeoutBeforeStore) throw new TaskCanceledException("fixture timeout");
                if (UploadError is { } uploadStatus)
                {
                    var response = Json(uploadStatus, "{\"message\":\"secret-provider-body\"}");
                    if (RateLimit) response.Headers.Add("X-RateLimit-Remaining", "0");
                    return response;
                }
                HasAsset = true;
                if (TimeoutAfterStore) throw new TaskCanceledException("fixture timeout");
                return Json(HttpStatusCode.Created, UploadBodyFactory?.Invoke(this) ?? UploadBody ?? AssetJson());
            }
            if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/releases/assets/555", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                    return HasAsset ? Json(HttpStatusCode.OK, AssetJson()) : Json(HttpStatusCode.NotFound, "{}");
                if (request.Method == HttpMethod.Delete)
                {
                    DeleteCalls++;
                    if (DeleteError is { } deleteStatus) return Json(deleteStatus, "{\"message\":\"secret-provider-body\"}");
                    HasAsset = false;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }
            throw new InvalidOperationException("Unexpected fake GitHub request.");
        }

        private string AssetJson() => JsonSerializer.Serialize(new
        {
            id = 555,
            name = AssetName,
            state = "uploaded",
            size = UploadedBytes.Length,
            digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(UploadedBytes)),
            browser_download_url = BrowserUrl,
        });

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
