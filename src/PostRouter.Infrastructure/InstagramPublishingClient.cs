using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PostRouter.Infrastructure;

internal sealed class InstagramPublishingException(string code, HttpStatusCode? status = null, bool ambiguous = false) : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
    public bool Ambiguous { get; } = ambiguous;
}

// Instagram Login publishing. The production HttpClient uses InstagramApiClient.CreateProductionHandler:
// no redirects, cookies, or HttpRequestOut events containing request URIs.
internal sealed class InstagramPublishingClient(HttpClient http)
{
    private const string Root = "https://graph.instagram.com/v26.0/";

    public Task<string> CreateContainerAsync(string accountId, string token, Uri videoUrl, string caption,
        bool shareToFeed, CancellationToken cancellationToken)
    {
        if (videoUrl.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(videoUrl.UserInfo))
            throw new InstagramPublishingException("instagram_staging_url_invalid");
        return PostIdAsync(accountId, "media", token,
            new Dictionary<string, string>
            {
                ["media_type"] = "REELS",
                ["video_url"] = videoUrl.AbsoluteUri,
                ["caption"] = caption,
                ["share_to_feed"] = shareToFeed ? "true" : "false",
            }, "instagram_container_create", cancellationToken);
    }

    public Task<string> PublishAsync(string accountId, string token, string containerId, CancellationToken cancellationToken) =>
        PostIdAsync(accountId, "media_publish", token,
            new Dictionary<string, string> { ["creation_id"] = Identifier(containerId) },
            "instagram_publish", cancellationToken);

    public async Task<string> GetContainerStatusAsync(string token, string containerId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + Identifier(containerId) + "?fields=status_code");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, "instagram_container_status", cancellationToken).ConfigureAwait(false);
        CheckResponse(response, "instagram_container_status");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("status_code", out var status) && status.ValueKind == JsonValueKind.String)
                return status.GetString() ?? string.Empty;
        }
        catch (Exception error) when (error is JsonException or IOException) { }
        throw new InstagramPublishingException("instagram_container_status_invalid");
    }

    private async Task<string> PostIdAsync(string accountId, string endpoint, string token,
        Dictionary<string, string> fields, string stage, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + Identifier(accountId) + "/" + endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, stage, cancellationToken).ConfigureAwait(false);
        CheckResponse(response, stage);
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return Identifier(id.GetString() ?? string.Empty);
        }
        catch (Exception error) when (error is JsonException or IOException) { }
        catch (InstagramPublishingException) { }
        // A successful HTTP response may have been applied even if its ID was lost.
        throw new InstagramPublishingException(stage + "_response_invalid", ambiguous: true);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string stage, CancellationToken cancellationToken)
    {
        try { return await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            // After SendAsync starts, transport failures cannot prove the POST was not received.
            throw new InstagramPublishingException(stage + "_transport_failure", ambiguous: request.Method == HttpMethod.Post);
        }
    }

    private static void CheckResponse(HttpResponseMessage response, string stage)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InstagramPublishingException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "instagram_auth_required",
            HttpStatusCode.TooManyRequests => "instagram_rate_limited",
            _ => stage + "_failed",
        }, response.StatusCode);
    }

    private static string Identifier(string value)
    {
        if (value.Length is < 1 or > 64 || !value.All(char.IsAsciiDigit))
            throw new InstagramPublishingException("instagram_remote_id_invalid");
        return value;
    }
}
