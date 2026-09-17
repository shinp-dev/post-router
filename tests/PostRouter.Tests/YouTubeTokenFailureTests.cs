using System.Net;
using System.Text;
using System.Text.Json;
using PostRouter.Gui;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class YouTubeTokenFailureTests
{
    private const string SecretDescription = "redirect_uri=secret-redirect authorization-code=secret-code code_verifier=secret-verifier client_secret=secret-client access_token=secret-token";

    [Theory]
    [InlineData("invalid_grant", "youtube_oauth_invalid_grant")]
    [InlineData("invalid_client", "youtube_oauth_invalid_client")]
    [InlineData("unauthorized_client", "youtube_oauth_unauthorized_client")]
    [InlineData("invalid_request", "youtube_oauth_invalid_request")]
    [InlineData("unsupported_grant_type", "youtube_oauth_unsupported_grant_type")]
    public async Task Token_endpoint_maps_known_error_only_to_fixed_safe_code(string googleError, string safeCode)
    {
        var body = JsonSerializer.Serialize(new { error = googleError, error_description = SecretDescription });
        using var http = Client(HttpStatusCode.BadRequest, body);
        var client = new YouTubeApiClient(http, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.ExchangeCodeAsync("desktop-client", new Uri("http://127.0.0.1:8765/callback"),
                "secret-code", "secret-verifier", CancellationToken.None));

        Assert.Equal(safeCode, failure.SafeCode);
        Assert.False(failure.Retryable);
        Assert.Null(failure.InnerException);
        var visible = failure + GuiApplication.OAuthFailurePage(failure);
        Assert.Contains(safeCode, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDescription, visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-redirect", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-code", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-verifier", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", visible, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"error\":\"unknown_secret_error\",\"error_description\":\"secret-description\"}")]
    [InlineData("{\"error\":42,\"error_description\":\"secret-description\"}")]
    [InlineData("{\"error_description\":\"secret-description\"}")]
    [InlineData("{invalid-json secret-description")]
    public async Task Unknown_or_malformed_token_error_uses_generic_code(string body)
    {
        using var http = Client(HttpStatusCode.BadRequest, body);
        var client = new YouTubeApiClient(http, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.ExchangeCodeAsync("desktop-client", new Uri("http://127.0.0.1:8765/callback"),
                "secret-code", "secret-verifier", CancellationToken.None));

        Assert.Equal("youtube_token_rejected", failure.SafeCode);
        var visible = failure + GuiApplication.OAuthFailurePage(failure);
        Assert.DoesNotContain("unknown_secret_error", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-description", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-code", visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_token_error_uses_generic_code()
    {
        using var http = Client(HttpStatusCode.BadRequest, new string('x', 1024 * 1024 + 1));
        var client = new YouTubeApiClient(http, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.ExchangeCodeAsync("desktop-client", new Uri("http://127.0.0.1:8765/callback"),
                "secret-code", "secret-verifier", CancellationToken.None));

        Assert.Equal("youtube_token_rejected", failure.SafeCode);
    }

    [Fact]
    public async Task Refresh_uses_same_token_error_mapping()
    {
        using var http = Client(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"secret-refresh-token\"}");
        var client = new YouTubeApiClient(http, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.RefreshAsync("desktop-client", "secret-refresh-token", CancellationToken.None));

        Assert.Equal("youtube_oauth_invalid_grant", failure.SafeCode);
        Assert.DoesNotContain("secret-refresh-token", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Transient_unrecognized_token_failure_keeps_retryability(HttpStatusCode status)
    {
        using var http = Client(status, "{\"error\":\"temporary_unknown\"}");
        var client = new YouTubeApiClient(http, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<YouTubeProviderException>(() =>
            client.RefreshAsync("desktop-client", "secret-refresh-token", CancellationToken.None));

        Assert.Equal("youtube_token_rejected", failure.SafeCode);
        Assert.True(failure.Retryable);
    }

    private static HttpClient Client(HttpStatusCode status, string body) =>
        new(new TokenErrorHandler(status, body));

    private sealed class TokenErrorHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("oauth2.googleapis.com", request.RequestUri?.Host);
            Assert.Equal("/token", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
