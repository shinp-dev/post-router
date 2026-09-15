using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class YouTubeSpecConformanceTests
{
    [Fact]
    public void Title_limit_counts_unicode_scalars_not_utf16_code_units()
    {
        var adapter = new YouTubeProviderAdapter(null!, null!, TimeProvider.System);
        var asset = new MediaAsset(Guid.NewGuid(), "sha", 4, "video/mp4", "unused.mp4");
        var target = new TargetIntent(Guid.NewGuid(), "youtube", "private", "youtube-options/v1", 1,
            "{\"madeForKids\":false}", "yt");
        var hundredEmoji = string.Concat(Enumerable.Repeat("😀", 100));
        var hundredOneEmoji = hundredEmoji + "😀";

        adapter.Validate(new Content(Guid.NewGuid(), ContentKind.Video, "description", hundredEmoji, [asset]), target);
        Assert.Throws<ArgumentException>(() =>
            adapter.Validate(new Content(Guid.NewGuid(), ContentKind.Video, "description", hundredOneEmoji, [asset]), target));
    }

    [Fact]
    public async Task Insert_metadata_carries_made_for_kids_and_synthetic_media_flags()
    {
        await using var context = await TestContext.CreateAsync();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var path = Path.Combine(context.Directory, "metadata.mp4");
        await File.WriteAllBytesAsync(path, bytes);
        var asset = new MediaAsset(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, "video/mp4", path);

        using var http = Client(async (request, _, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("\"privacyStatus\":\"private\"", body, StringComparison.Ordinal);
            Assert.Contains("\"selfDeclaredMadeForKids\":true", body, StringComparison.Ordinal);
            Assert.Contains("\"containsSyntheticMedia\":true", body, StringComparison.Ordinal);
            var response = Json(HttpStatusCode.OK, "{}");
            response.Headers.Location = Session("metadata-session");
            return response;
        });
        var client = new YouTubeApiClient(http, context.Time);

        var session = await client.StartResumableUploadAsync(
            "access", asset, "title", "description", new YouTubeOptions(true, true), CancellationToken.None);

        Assert.Contains("upload_id=metadata-session", session.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_error_reason_distinguishes_quota_from_authentication()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"quotaExceeded\",\"message\":\"secret provider message\"}]}}")));
        var client = new YouTubeApiClient(http, context.Time);

        var error = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.GetVideoAsync("access", "video-id", CancellationToken.None));

        Assert.Equal("youtube_quota_exceeded", error.SafeCode);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_incomplete_honors_retry_after_without_consuming_progress_semantics()
    {
        await using var context = await TestContext.CreateAsync();
        var expected = context.Time.GetUtcNow().AddSeconds(45);
        using var http = Client((_, _, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)308);
            response.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        });
        var client = new YouTubeApiClient(http, context.Time);

        var result = await client.QueryUploadAsync("access", Session("retry-after"), 6, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(2, result.NextOffset);
        Assert.Equal(expected, result.RetryAt);
    }

    [Fact]
    public async Task Processing_failure_uses_official_processing_failure_reason_first()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"items\":[{\"id\":\"video-failed\",\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"private\"},\"processingDetails\":{\"processingStatus\":\"failed\",\"processingFailureReason\":\"transcodeFailed\"},\"suggestions\":{\"processingErrors\":[\"audioFile\"]}}]}")));
        var client = new YouTubeApiClient(http, context.Time);
        var authProvider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "youtube-spec-auth-locks")), context.Gate, [authProvider], context.Time);
        var account = await SaveConnectionAsync(context);
        var adapter = new YouTubeProviderAdapter(auth, client, context.Time);
        var asset = new MediaAsset(Guid.NewGuid(), "sha", 4, "video/mp4", "unused.mp4");
        var checkpoint = new YouTubeCheckpoint(VideoId: "video-failed", Stage: "processing");
        var plan = new YouTubePlan(account.AccountId, "poll-processing", asset, "title", "description", "public",
            new YouTubeOptions(false, null), checkpoint);
        var opaque = JsonSerializer.Serialize(plan);
        var step = new ProviderStep("youtube.poll-processing.v1", StepEffect.ReadOnly, ReplaySafety.SafeRead,
            "digest", context.Time.GetUtcNow(), OpaquePlan: opaque);

        var result = await adapter.ExecuteStepAsync(step, CancellationToken.None);

        Assert.Equal(StepOutcome.Rejected, result.Outcome);
        Assert.Equal("youtube_processing_failed_transcodeFailed", result.SafeError);
        Assert.Equal(PublicationState.NeedsAttention, result.ObservedState);
    }

    [Fact]
    public void Desktop_oauth_rejects_other_loopback_addresses()
    {
        using var http = Client((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var provider = new YouTubeAuthProvider(new YouTubeApiClient(http, TimeProvider.System), TimeProvider.System);

        Assert.Throws<ArgumentException>(() =>
            provider.BeginAuthorization("client", new Uri("http://127.0.0.2:43127/callback")));
        _ = provider.BeginAuthorization("client", new Uri("http://127.0.0.1:43127/callback"));
        _ = provider.BeginAuthorization("client", new Uri("http://[::1]:43127/callback"));
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context)
    {
        var expires = context.Time.GetUtcNow().AddHours(2);
        var material = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("access", "refresh", expires));
        var blob = await context.Store.PutAsync("auth-token", material);
        return await context.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-spec", "channel-spec", "YouTube Spec", "desktop-client",
            YouTubeAuthProvider.RequiredScope, expires, blob));
    }

    private static Uri Session(string id) => new($"https://www.googleapis.com/upload/youtube/v3/videos?upload_id={id}");

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
}
