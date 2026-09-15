using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

internal sealed class YouTubeProviderException(string safeCode, bool retryable, Exception? inner = null)
    : ProviderOperationException(safeCode, retryable, inner);

internal sealed record YouTubeFailureMapping(string SafeCode, bool Retryable);

internal sealed class YouTubeApiClient(HttpClient httpClient, TimeProvider timeProvider)
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private static readonly Uri RevokeEndpoint = new("https://oauth2.googleapis.com/revoke");
    private static readonly Uri ChannelsEndpoint = new("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true&maxResults=2");
    private static readonly Uri StartUploadEndpoint = new("https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
    private static readonly string[] UploadHosts = ["www.googleapis.com", "youtube.googleapis.com"];
    private readonly HttpClient _http = httpClient;

    public Task<YouTubeTokenResponse> ExchangeCodeAsync(
        string clientId,
        Uri redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken) =>
        SendTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        }, cancellationToken);

    public Task<YouTubeTokenResponse> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken) =>
        SendTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, cancellationToken);

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapFailureAsync(response, "youtube_revoke_rejected", cancellationToken).ConfigureAwait(false);
    }

    public async Task<YouTubeChannel> GetCurrentChannelAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, ChannelsEndpoint, accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapFailureAsync(response, "youtube_channel_lookup_rejected", cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<YouTubeChannelsEnvelope>(bytes);
            if (envelope?.Items is null || envelope.Items.Count != 1 || string.IsNullOrWhiteSpace(envelope.Items[0].Id))
                throw new YouTubeProviderException("youtube_channel_identity_ambiguous", false);
            return envelope.Items[0];
        }
        catch (JsonException ex) { throw new YouTubeProviderException("youtube_channel_lookup_malformed", false, ex); }
    }

    public async Task<Uri> StartResumableUploadAsync(
        string accessToken,
        MediaAsset asset,
        string title,
        string description,
        YouTubeOptions options,
        CancellationToken cancellationToken)
    {
        await VerifyMediaAsync(asset, cancellationToken).ConfigureAwait(false);
        using var request = Authorized(HttpMethod.Post, StartUploadEndpoint, accessToken);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", asset.SizeBytes.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", asset.DetectedMime);
        var metadata = new YouTubeInsertRequest(
            new YouTubeSnippetWrite(title, description),
            new YouTubeStatusWrite("private", options.MadeForKids, options.ContainsSyntheticMedia));
        request.Content = JsonContent(metadata);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapFailureAsync(response, "youtube_upload_session_rejected", cancellationToken).ConfigureAwait(false);
        if (response.Headers.Location is null || !ValidUploadSession(response.Headers.Location))
            throw new YouTubeProviderException("youtube_upload_session_malformed", false);
        return response.Headers.Location;
    }

    public async Task<YouTubeUploadResult> QueryUploadAsync(
        string accessToken,
        Uri sessionUri,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        ValidateUploadSession(sessionUri);
        using var request = Authorized(HttpMethod.Put, sessionUri, accessToken);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes);
        HttpResponseMessage response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (YouTubeProviderException ex) when (ex.Retryable)
        {
            return new(false, null, 0, false, ex.SafeCode, NeedsStatusQuery: true, RetryAt: timeProvider.GetUtcNow().AddSeconds(10));
        }
        using (response)
        {
            if ((int)response.StatusCode == 308)
                return new(false, null, NextOffset(response), false, null, RetryAt: RetryAt(response));
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(false, null, 0, true, "youtube_upload_session_expired");
            if (!response.IsSuccessStatusCode)
                throw await MapFailureAsync(response, "youtube_upload_status_rejected", cancellationToken).ConfigureAwait(false);
            return await CompletedUploadAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<YouTubeUploadResult> UploadRemainingAsync(
        string accessToken,
        Uri sessionUri,
        MediaAsset asset,
        long offset,
        CancellationToken cancellationToken)
    {
        ValidateUploadSession(sessionUri);
        if (offset < 0 || offset >= asset.SizeBytes) throw new YouTubeProviderException("youtube_upload_offset_invalid", false);
        await VerifyMediaAsync(asset, cancellationToken).ConfigureAwait(false);

        await using var stream = new FileStream(asset.StorageRef, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = offset;
        using var request = Authorized(HttpMethod.Put, sessionUri, accessToken);
        request.Content = new StreamContent(stream, 1024 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(asset.DetectedMime);
        request.Content.Headers.ContentLength = asset.SizeBytes - offset;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, asset.SizeBytes - 1, asset.SizeBytes);

        HttpResponseMessage response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (YouTubeProviderException ex) when (ex.Retryable)
        {
            return new(false, null, offset, false, ex.SafeCode, NeedsStatusQuery: true, RetryAt: timeProvider.GetUtcNow().AddSeconds(10));
        }
        using (response)
        {
            if ((int)response.StatusCode == 308)
            {
                var next = NextOffset(response);
                return new(false, null, next, false, next > offset ? null : "youtube_upload_no_progress", RetryAt: RetryAt(response));
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(false, null, 0, true, "youtube_upload_session_expired");
            if ((int)response.StatusCode >= 500)
                return new(false, null, offset, false, "youtube_upload_response_uncertain", NeedsStatusQuery: true, RetryAt: RetryAt(response) ?? timeProvider.GetUtcNow().AddSeconds(15));
            if (!response.IsSuccessStatusCode)
                throw await MapFailureAsync(response, "youtube_upload_rejected", cancellationToken).ConfigureAwait(false);
            return await CompletedUploadAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<YouTubeVideoObservation> GetVideoAsync(string accessToken, string videoId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://www.googleapis.com/youtube/v3/videos?part=status,processingDetails,suggestions&id={Uri.EscapeDataString(videoId)}");
        using var request = Authorized(HttpMethod.Get, uri, accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapFailureAsync(response, "youtube_video_status_rejected", cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<YouTubeVideosEnvelope>(bytes);
            if (envelope?.Items is null || envelope.Items.Count != 1 ||
                !string.Equals(envelope.Items[0].Id, videoId, StringComparison.Ordinal))
                throw new YouTubeProviderException("youtube_video_not_found", false);
            var item = envelope.Items[0];
            if (item.Status is null) throw new YouTubeProviderException("youtube_video_status_malformed", false);
            return new(videoId, item.Status, item.ProcessingDetails, item.Suggestions);
        }
        catch (JsonException ex) { throw new YouTubeProviderException("youtube_video_status_malformed", false, ex); }
    }

    public async Task<YouTubeUpdateResult> UpdateVisibilityAsync(
        string accessToken,
        string videoId,
        string desiredVisibility,
        YouTubeStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var uri = new Uri("https://www.googleapis.com/youtube/v3/videos?part=status");
        using var request = Authorized(HttpMethod.Put, uri, accessToken);
        var status = new Dictionary<string, object?>
        {
            ["privacyStatus"] = desiredVisibility,
        };
        if (snapshot.Embeddable is not null) status["embeddable"] = snapshot.Embeddable.Value;
        if (snapshot.License is not null) status["license"] = snapshot.License;
        if (snapshot.PublicStatsViewable is not null) status["publicStatsViewable"] = snapshot.PublicStatsViewable.Value;
        if (snapshot.SelfDeclaredMadeForKids is not null) status["selfDeclaredMadeForKids"] = snapshot.SelfDeclaredMadeForKids.Value;
        if (snapshot.ContainsSyntheticMedia is not null) status["containsSyntheticMedia"] = snapshot.ContainsSyntheticMedia.Value;
        request.Content = JsonContent(new Dictionary<string, object?> { ["id"] = videoId, ["status"] = status });

        HttpResponseMessage response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (YouTubeProviderException ex) when (ex.Retryable)
        {
            return new(false, true, ex.SafeCode);
        }
        using (response)
        {
            if ((int)response.StatusCode >= 500) return new(false, true, "youtube_publish_server_uncertain");
            if (!response.IsSuccessStatusCode)
            {
                var failure = await MapFailureAsync(response, "youtube_publish_rejected", cancellationToken).ConfigureAwait(false);
                DateTimeOffset? retryAt = failure.SafeCode == "youtube_rate_limited"
                    ? RetryAt(response) ?? timeProvider.GetUtcNow().AddMinutes(15)
                    : null;
                return new(false, false, failure.SafeCode, retryAt);
            }
            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            try
            {
                var item = JsonSerializer.Deserialize<YouTubeVideoItem>(bytes);
                if (item is null || !string.Equals(item.Id, videoId, StringComparison.Ordinal) || item.Status is null ||
                    !string.Equals(item.Status.PrivacyStatus, desiredVisibility, StringComparison.Ordinal))
                    return new(false, true, "youtube_publish_malformed_success");
                return new(true, false, null);
            }
            catch (JsonException) { return new(false, true, "youtube_publish_malformed_success"); }
        }
    }

    private async Task<YouTubeTokenResponse> SendTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapFailureAsync(response, "youtube_token_rejected", cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var token = JsonSerializer.Deserialize<YouTubeTokenResponse>(bytes);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
                throw new YouTubeProviderException("youtube_token_malformed", false);
            return token;
        }
        catch (JsonException ex) { throw new YouTubeProviderException("youtube_token_malformed", false, ex); }
    }

    private static async Task<YouTubeUploadResult> CompletedUploadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var item = JsonSerializer.Deserialize<YouTubeVideoItem>(bytes);
            if (item is null || string.IsNullOrWhiteSpace(item.Id)) throw new YouTubeProviderException("youtube_upload_success_malformed", false);
            return new(true, item.Id, 0, false, null);
        }
        catch (JsonException ex) { throw new YouTubeProviderException("youtube_upload_success_malformed", false, ex); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new YouTubeProviderException("youtube_network_unavailable", true, ex); }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static ByteArrayContent JsonContent<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static bool ValidUploadSession(Uri uri) =>
        uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        UploadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static void ValidateUploadSession(Uri uri)
    {
        if (!ValidUploadSession(uri)) throw new YouTubeProviderException("youtube_upload_session_invalid", false);
    }

    private static long NextOffset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var dash = value.LastIndexOf('-');
        if (dash < 0 || !long.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last) || last < 0)
            throw new YouTubeProviderException("youtube_upload_range_malformed", false);
        return checked(last + 1);
    }

    private DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return timeProvider.GetUtcNow().Add(delta);
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        return null;
    }

    private static async Task VerifyMediaAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        if (asset.SizeBytes <= 0 || !File.Exists(asset.StorageRef)) throw new YouTubeProviderException("media_unavailable", false);
        await using var stream = new FileStream(asset.StorageRef, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != asset.SizeBytes) throw new YouTubeProviderException("media_integrity_mismatch", false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, asset.Sha256, StringComparison.Ordinal))
            throw new YouTubeProviderException("media_integrity_mismatch", false);
    }

    private static async Task<YouTubeProviderException> MapFailureAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken)
    {
        var reason = await TryReadErrorReasonAsync(response, cancellationToken).ConfigureAwait(false);
        var mapping = MapSafeFailure(response.StatusCode, reason, fallback);
        return new(mapping.SafeCode, mapping.Retryable);
    }

    private static YouTubeFailureMapping MapSafeFailure(HttpStatusCode status, string? reason, string fallback)
    {
        var byReason = reason switch
        {
            "rateLimitExceeded" or "userRateLimitExceeded" => new YouTubeFailureMapping("youtube_rate_limited", true),
            "quotaExceeded" or "dailyLimitExceeded" => new YouTubeFailureMapping("youtube_quota_exceeded", false),
            "uploadLimitExceeded" => new YouTubeFailureMapping("youtube_upload_limit_exceeded", false),
            "insufficientPermissions" or "authError" => new YouTubeFailureMapping("youtube_auth_or_scope_rejected", false),
            "forbiddenPrivacySetting" or "forbiddenLicenseSetting" => new YouTubeFailureMapping("youtube_policy_rejected", false),
            "channelSuspended" => new YouTubeFailureMapping("youtube_channel_suspended", false),
            "forbidden" => new YouTubeFailureMapping("youtube_forbidden", false),
            "mediaBodyRequired" => new YouTubeFailureMapping("youtube_invalid_input", false),
            not null when reason.StartsWith("invalid", StringComparison.Ordinal) => new YouTubeFailureMapping("youtube_invalid_input", false),
            _ => null,
        };
        if (byReason is not null) return byReason;
        if (status == HttpStatusCode.TooManyRequests) return new("youtube_rate_limited", true);
        if (status == HttpStatusCode.Unauthorized) return new("youtube_auth_or_scope_rejected", false);
        if (status == HttpStatusCode.Forbidden) return new("youtube_forbidden", false);
        if ((int)status >= 500) return new("youtube_temporary_unavailable", true);
        return new(fallback, false);
    }

    private static async Task<string?> TryReadErrorReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            var envelope = JsonSerializer.Deserialize<GoogleApiErrorEnvelope>(bytes);
            return envelope?.Error?.Errors?
                .Select(error => error.Reason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        }
        catch (Exception ex) when (ex is JsonException or YouTubeProviderException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new YouTubeProviderException("youtube_response_too_large", false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes) throw new YouTubeProviderException("youtube_response_too_large", false);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

internal sealed record GoogleApiErrorEnvelope([property: JsonPropertyName("error")] GoogleApiError? Error);
internal sealed record GoogleApiError([property: JsonPropertyName("errors")] List<GoogleApiErrorDetail>? Errors);
internal sealed record GoogleApiErrorDetail([property: JsonPropertyName("reason")] string? Reason);
internal sealed record YouTubeTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("token_type")] string? TokenType);
internal sealed record YouTubeChannelsEnvelope([property: JsonPropertyName("items")] List<YouTubeChannel>? Items);
internal sealed record YouTubeChannel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("snippet")] YouTubeChannelSnippet? Snippet);
internal sealed record YouTubeChannelSnippet([property: JsonPropertyName("title")] string? Title);
internal sealed record YouTubeInsertRequest(
    [property: JsonPropertyName("snippet")] YouTubeSnippetWrite Snippet,
    [property: JsonPropertyName("status")] YouTubeStatusWrite Status);
internal sealed record YouTubeSnippetWrite(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description);
internal sealed record YouTubeStatusWrite(
    [property: JsonPropertyName("privacyStatus")] string PrivacyStatus,
    [property: JsonPropertyName("selfDeclaredMadeForKids")] bool SelfDeclaredMadeForKids,
    [property: JsonPropertyName("containsSyntheticMedia")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ContainsSyntheticMedia);
internal sealed record YouTubeVideosEnvelope([property: JsonPropertyName("items")] List<YouTubeVideoItem>? Items);
internal sealed record YouTubeVideoItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] YouTubeVideoStatus? Status = null,
    [property: JsonPropertyName("processingDetails")] YouTubeProcessingDetails? ProcessingDetails = null,
    [property: JsonPropertyName("suggestions")] YouTubeSuggestions? Suggestions = null);
internal sealed record YouTubeVideoStatus(
    [property: JsonPropertyName("uploadStatus")] string? UploadStatus,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("privacyStatus")] string? PrivacyStatus,
    [property: JsonPropertyName("publishAt")] DateTimeOffset? PublishAt,
    [property: JsonPropertyName("embeddable")] bool? Embeddable,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("publicStatsViewable")] bool? PublicStatsViewable,
    [property: JsonPropertyName("selfDeclaredMadeForKids")] bool? SelfDeclaredMadeForKids,
    [property: JsonPropertyName("containsSyntheticMedia")] bool? ContainsSyntheticMedia);
internal sealed record YouTubeProcessingDetails(
    [property: JsonPropertyName("processingStatus")] string? ProcessingStatus,
    [property: JsonPropertyName("processingFailureReason")] string? ProcessingFailureReason);
internal sealed record YouTubeSuggestions([property: JsonPropertyName("processingErrors")] List<string>? ProcessingErrors);
internal sealed record YouTubeVideoObservation(
    string VideoId, YouTubeVideoStatus Status, YouTubeProcessingDetails? ProcessingDetails, YouTubeSuggestions? Suggestions);
internal sealed record YouTubeUploadResult(
    bool Completed, string? VideoId, long NextOffset, bool SessionExpired, string? SafeError,
    bool NeedsStatusQuery = false, DateTimeOffset? RetryAt = null);
internal sealed record YouTubeUpdateResult(bool Success, bool Ambiguous, string? SafeError, DateTimeOffset? RetryAt = null);
internal sealed record YouTubeStatusSnapshot(
    string PrivacyStatus,
    bool? Embeddable,
    string? License,
    bool? PublicStatsViewable,
    bool? SelfDeclaredMadeForKids,
    bool? ContainsSyntheticMedia);
internal sealed record YouTubeCheckpoint(
    int Version = 1,
    string? SessionUri = null,
    long NextOffset = 0,
    bool NeedsStatusQuery = false,
    string? VideoId = null,
    string Stage = "new",
    YouTubeStatusSnapshot? Status = null,
    DateTimeOffset? StatusCheckedAt = null);
internal sealed record YouTubeOptions(bool MadeForKids = false, bool? ContainsSyntheticMedia = null);
internal sealed record YouTubePlan(
    Guid AccountId,
    string Operation,
    MediaAsset Asset,
    string Title,
    string Description,
    string Visibility,
    YouTubeOptions Options,
    YouTubeCheckpoint Checkpoint);

internal sealed class YouTubeAuthProvider(YouTubeApiClient client, TimeProvider timeProvider) : IInteractiveAuthProvider
{
    public const string RequiredScope = "https://www.googleapis.com/auth/youtube.force-ssl";
    public string ProviderKey => "youtube";

    public AuthorizationSession BeginAuthorization(
        string clientId,
        Uri redirectUri,
        Guid? expectedAccountId = null,
        string? expectedSubject = null,
        string? requestedAlias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ValidateRedirect(redirectUri);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = RequiredScope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
        };
        var encoded = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new(ProviderKey, clientId, redirectUri, new Uri($"https://accounts.google.com/o/oauth2/v2/auth?{encoded}"),
            state, verifier, RequiredScope, expectedAccountId, expectedSubject, requestedAlias);
    }

    public async Task<ConnectedIdentity> CompleteAuthorizationAsync(
        AuthorizationSession session,
        string code,
        string returnedState,
        CancellationToken cancellationToken)
    {
        if (!FixedEquals(session.State, returnedState)) throw new InvalidOperationException("oauth_state_mismatch");
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var token = await client.ExchangeCodeAsync(session.ClientId, session.RedirectUri, code, session.CodeVerifier, cancellationToken).ConfigureAwait(false);
        var scope = string.IsNullOrWhiteSpace(token.Scope) ? session.Scope : token.Scope;
        if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(RequiredScope, StringComparer.Ordinal))
            throw new InvalidOperationException("oauth_scope_missing");
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw new InvalidOperationException("oauth_refresh_token_missing");
        var channel = await client.GetCurrentChannelAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
        return new(channel.Id, channel.Snippet?.Title ?? channel.Id, scope,
            new TokenMaterial(token.AccessToken, token.RefreshToken, timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn)));
    }

    public async Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant.ClientId) || string.IsNullOrWhiteSpace(current.RefreshToken))
            throw new YouTubeProviderException("youtube_reconnect_required", false);
        var token = await client.RefreshAsync(grant.ClientId, current.RefreshToken, cancellationToken).ConfigureAwait(false);
        return new(new TokenMaterial(token.AccessToken, token.RefreshToken ?? current.RefreshToken,
            timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn)), "youtube-token-refresh");
    }

    public Task RevokeAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken) =>
        client.RevokeAsync(current.RefreshToken ?? current.AccessToken, cancellationToken);

    private static void ValidateRedirect(Uri redirectUri)
    {
        var validHost = IPAddress.TryParse(redirectUri.Host, out var address) &&
            (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
        if (!redirectUri.IsAbsoluteUri || !string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !validHost)
            throw new ArgumentException("YouTube Desktop OAuth redirect must use 127.0.0.1 or ::1 over HTTP.");
        if (redirectUri.IsDefaultPort) throw new ArgumentException("YouTube OAuth redirect must include the loopback listener port.");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

internal sealed class YouTubeProviderAdapter(AuthCoordinator auth, YouTubeApiClient client, TimeProvider timeProvider) : IProviderAdapter
{
    private static readonly TimeSpan PublishSnapshotLifetime = TimeSpan.FromMinutes(1);
    private const long MaxVideoBytes = 256L * 1024 * 1024 * 1024;

    public string ProviderKey => "youtube";
    public ProviderCapabilities Capabilities { get; } = new(
        "youtube", [ContentKind.Video], ["private", "unlisted", "public"], "youtube-options/v1", 1, "{}", true, true);
    public bool RequiresConnectedAccount => true;

    public void Validate(Content content, TargetIntent target)
    {
        if (content.Kind != ContentKind.Video || content.MediaAssets.Count != 1)
            throw new NotSupportedException("YouTube Phase 3 supports exactly one video asset per post.");
        var asset = content.MediaAssets[0];
        if (asset.SizeBytes is <= 0 or > MaxVideoBytes ||
            !(asset.DetectedMime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(asset.DetectedMime, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("YouTube video must be a supported video MIME type and no larger than 256 GB.");
        var title = content.Title;
        if (string.IsNullOrWhiteSpace(title) || title.EnumerateRunes().Count() > 100 || title.Contains('<') || title.Contains('>'))
            throw new ArgumentException("YouTube title must contain 1 to 100 Unicode scalar values and no angle brackets.");
        var description = content.Text ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(description) > 5000 || description.Contains('<') || description.Contains('>'))
            throw new ArgumentException("YouTube description must be at most 5000 UTF-8 bytes and contain no angle brackets.");
        if (target.Visibility is not ("private" or "unlisted" or "public"))
            throw new NotSupportedException("YouTube visibility must be private, unlisted, or public.");
        if (!string.Equals(target.OptionsSchema, "youtube-options/v1", StringComparison.Ordinal) || target.OptionsVersion != 1)
            throw new ArgumentException("YouTube requires youtube-options/v1 options version 1.");
        _ = ParseOptions(target.CanonicalOptionsJson);
    }

    public Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken)
    {
        Validate(input.Content, input.Target);
        if (!TryCheckpoint(checkpoint, out var state))
            return Task.FromResult(Step(input, "invalid-checkpoint", StepEffect.ReadOnly, ReplaySafety.SafeRead, new()));

        if (state.VideoId is null)
        {
            if (state.SessionUri is null)
                return Task.FromResult(Step(input, "start-upload", StepEffect.CreateRemoteObject, ReplaySafety.SafeRepeatNoPublication, state));
            if (state.NeedsStatusQuery)
                return Task.FromResult(Step(input, "query-upload", StepEffect.ReadOnly, ReplaySafety.SafeRead, state));
            return Task.FromResult(Step(input, "upload", StepEffect.UploadOnly, ReplaySafety.ResumeKnownHandle, state));
        }

        if (!string.Equals(state.Stage, "ready", StringComparison.Ordinal))
            return Task.FromResult(Step(input, "poll-processing", StepEffect.ReadOnly, ReplaySafety.SafeRead, state));

        if (string.Equals(input.Target.Visibility, "private", StringComparison.Ordinal))
            return Task.FromResult(Step(input, "finish-private", StepEffect.ReadOnly, ReplaySafety.SafeRead, state));

        if (state.Status is null || state.StatusCheckedAt is null ||
            state.StatusCheckedAt.Value.Add(PublishSnapshotLifetime) <= timeProvider.GetUtcNow())
            return Task.FromResult(Step(input, "refresh-status", StepEffect.ReadOnly, ReplaySafety.SafeRead, state));

        return Task.FromResult(Step(input, "publish", StepEffect.MayPublish, ReplaySafety.IdempotentExistingObject, state));
    }

    public async Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        YouTubePlan plan;
        try { plan = JsonSerializer.Deserialize<YouTubePlan>(providerStep.OpaquePlan ?? string.Empty) ?? throw new JsonException(); }
        catch (JsonException) { return Reject("youtube_plan_invalid", FailureCategory.InvalidInput); }
        if (plan.Operation == "invalid-checkpoint") return Reject("youtube_checkpoint_invalid", FailureCategory.InvalidInput, PublicationState.NeedsAttention);

        TokenMaterial token;
        try { token = await auth.GetValidTokenAsync(plan.AccountId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (YouTubeProviderException ex) { return ProviderFailure(ex); }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "auth_required", StringComparison.Ordinal))
        { return Reject("auth_required", FailureCategory.Authentication, PublicationState.NeedsAttention); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or KeyNotFoundException)
        { return Reject("credential_unavailable", FailureCategory.Authentication, PublicationState.NeedsAttention); }

        try
        {
            return plan.Operation switch
            {
                "start-upload" => await StartAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "query-upload" => await QueryAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "upload" => await UploadAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "poll-processing" => await ObserveAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "refresh-status" => await RefreshStatusAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                "finish-private" => new(StepOutcome.Completed, EffectCertainty.Confirmed, plan.Checkpoint.VideoId,
                    Checkpoint: JsonSerializer.Serialize(plan.Checkpoint), ObservedState: PublicationState.Published),
                "publish" => await PublishAsync(plan, token.AccessToken, cancellationToken).ConfigureAwait(false),
                _ => Reject("youtube_plan_operation_unknown", FailureCategory.InvalidInput),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (YouTubeProviderException ex) { return ProviderFailure(ex); }
    }

    public async Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken)
    {
        if (!TryCheckpoint(checkpoint, out var state) || string.IsNullOrWhiteSpace(state.VideoId))
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "youtube_reconcile_missing_video_id",
                ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.Unknown);
        TokenMaterial token;
        try { token = await auth.GetValidTokenAsync(input.Publication.AccountId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: "youtube_reconcile_auth_unavailable",
                RetryAt: timeProvider.GetUtcNow().AddMinutes(5), FailureCategory: FailureCategory.Authentication);
        }
        try
        {
            var observed = await client.GetVideoAsync(token.AccessToken, state.VideoId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(observed.Status.PrivacyStatus, input.Target.Visibility, StringComparison.Ordinal))
                return new(StepOutcome.Completed, EffectCertainty.Confirmed, state.VideoId, Checkpoint: checkpoint,
                    ObservedState: PublicationState.Published);
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: checkpoint,
                SafeError: "youtube_publish_not_observed", RetryAt: timeProvider.GetUtcNow().AddMinutes(5),
                FailureCategory: FailureCategory.Unknown);
        }
        catch (YouTubeProviderException ex)
        {
            return ex.Retryable
                ? new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: checkpoint, SafeError: ex.SafeCode,
                    RetryAt: timeProvider.GetUtcNow().AddMinutes(5), FailureCategory: Classify(ex.SafeCode))
                : Reject(ex.SafeCode, Classify(ex.SafeCode), PublicationState.NeedsAttention);
        }
    }

    private async Task<StepResult> StartAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        var session = await client.StartResumableUploadAsync(
            token, plan.Asset, plan.Title, plan.Description, plan.Options, cancellationToken).ConfigureAwait(false);
        var next = plan.Checkpoint with { SessionUri = session.AbsoluteUri, NextOffset = 0, NeedsStatusQuery = false, Stage = "uploading" };
        return Continue(next, JobKind.Publish);
    }

    private async Task<StepResult> QueryAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(plan.Checkpoint.SessionUri, UriKind.Absolute, out var session)) return Reject("youtube_upload_session_invalid", FailureCategory.InvalidInput, PublicationState.NeedsAttention);
        var result = await client.QueryUploadAsync(token, session, plan.Asset.SizeBytes, cancellationToken).ConfigureAwait(false);
        return UploadResult(plan, result, query: true);
    }

    private async Task<StepResult> UploadAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(plan.Checkpoint.SessionUri, UriKind.Absolute, out var session)) return Reject("youtube_upload_session_invalid", FailureCategory.InvalidInput, PublicationState.NeedsAttention);
        var result = await client.UploadRemainingAsync(token, session, plan.Asset, plan.Checkpoint.NextOffset, cancellationToken).ConfigureAwait(false);
        return UploadResult(plan, result, query: false);
    }

    private StepResult UploadResult(YouTubePlan plan, YouTubeUploadResult result, bool query)
    {
        if (result.Completed && result.VideoId is not null)
        {
            var next = plan.Checkpoint with
            {
                VideoId = result.VideoId,
                SessionUri = null,
                NextOffset = 0,
                NeedsStatusQuery = false,
                Stage = "processing",
            };
            return Continue(next, JobKind.Poll, timeProvider.GetUtcNow(), PublicationState.Processing);
        }
        if (result.SessionExpired)
        {
            var reset = plan.Checkpoint with { SessionUri = null, NextOffset = 0, NeedsStatusQuery = false, Stage = "new" };
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: JsonSerializer.Serialize(reset),
                SafeError: result.SafeError, RetryAt: result.RetryAt ?? timeProvider.GetUtcNow(),
                FailureCategory: FailureCategory.Network);
        }
        var queryFailed = query && result.SafeError is not null;
        var nextState = plan.Checkpoint with
        {
            NextOffset = queryFailed ? plan.Checkpoint.NextOffset : Math.Clamp(result.NextOffset, 0, plan.Asset.SizeBytes),
            NeedsStatusQuery = queryFailed || result.NeedsStatusQuery,
            Stage = "uploading",
        };
        var normalProgress = result.SafeError is null && !result.NeedsStatusQuery;
        return new(StepOutcome.Pending,
            result.NeedsStatusQuery ? EffectCertainty.Ambiguous : EffectCertainty.Confirmed,
            Checkpoint: JsonSerializer.Serialize(nextState), SafeError: result.SafeError,
            RetryAt: result.RetryAt ?? timeProvider.GetUtcNow(), FailureCategory: result.SafeError is null ? null : FailureCategory.Network,
            NextJobKind: normalProgress ? JobKind.Publish : null);
    }

    private async Task<StepResult> ObserveAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        var videoId = plan.Checkpoint.VideoId!;
        var observed = await client.GetVideoAsync(token, videoId, cancellationToken).ConfigureAwait(false);
        var upload = observed.Status.UploadStatus;
        var processing = observed.ProcessingDetails?.ProcessingStatus;
        if (string.Equals(upload, "failed", StringComparison.Ordinal))
            return Reject(KnownReason("youtube_upload_failed", observed.Status.FailureReason), FailureCategory.InvalidInput);
        if (string.Equals(upload, "rejected", StringComparison.Ordinal))
            return Reject(KnownReason("youtube_upload_rejected", observed.Status.RejectionReason), FailureCategory.Provider);
        if (string.Equals(processing, "failed", StringComparison.Ordinal))
        {
            var officialReason = observed.ProcessingDetails?.ProcessingFailureReason;
            var diagnosticReason = observed.Suggestions?.ProcessingErrors?.FirstOrDefault();
            var reason = officialReason ?? diagnosticReason;
            var category = officialReason is null ? FailureCategory.InvalidInput : FailureCategory.Provider;
            return Reject(KnownReason("youtube_processing_failed", reason), category, PublicationState.NeedsAttention);
        }
        if (string.Equals(processing, "terminated", StringComparison.Ordinal))
            return Reject("youtube_processing_terminated", FailureCategory.Provider, PublicationState.NeedsAttention);
        if (string.Equals(upload, "processed", StringComparison.Ordinal) && string.Equals(processing, "succeeded", StringComparison.Ordinal))
        {
            var next = plan.Checkpoint with
            {
                Stage = "ready",
                Status = Snapshot(observed.Status),
                StatusCheckedAt = timeProvider.GetUtcNow(),
            };
            return Continue(next, JobKind.Publish, timeProvider.GetUtcNow(), PublicationState.Ready);
        }
        if (upload is "uploaded" or "processed" || processing is "processing")
            return Continue(plan.Checkpoint, JobKind.Poll, timeProvider.GetUtcNow().AddSeconds(30), PublicationState.Processing);
        return Reject("youtube_processing_status_unknown", FailureCategory.Provider, PublicationState.NeedsAttention);
    }

    private async Task<StepResult> RefreshStatusAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        var observed = await client.GetVideoAsync(token, plan.Checkpoint.VideoId!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(observed.Status.UploadStatus, "processed", StringComparison.Ordinal) ||
            !string.Equals(observed.ProcessingDetails?.ProcessingStatus, "succeeded", StringComparison.Ordinal))
        {
            var processing = plan.Checkpoint with { Stage = "processing" };
            return Continue(processing, JobKind.Poll, timeProvider.GetUtcNow().AddSeconds(30), PublicationState.Processing);
        }
        var next = plan.Checkpoint with { Status = Snapshot(observed.Status), StatusCheckedAt = timeProvider.GetUtcNow(), Stage = "ready" };
        return Continue(next, JobKind.Publish, timeProvider.GetUtcNow(), PublicationState.Ready);
    }

    private async Task<StepResult> PublishAsync(YouTubePlan plan, string token, CancellationToken cancellationToken)
    {
        if (plan.Checkpoint.Status is null) return Reject("youtube_publish_snapshot_missing", FailureCategory.InvalidInput, PublicationState.NeedsAttention);
        var result = await client.UpdateVisibilityAsync(token, plan.Checkpoint.VideoId!, plan.Visibility, plan.Checkpoint.Status, cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return new(StepOutcome.Completed, EffectCertainty.Confirmed, plan.Checkpoint.VideoId,
                Checkpoint: JsonSerializer.Serialize(plan.Checkpoint), ObservedState: PublicationState.Published);
        if (result.Ambiguous)
            return new(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, Checkpoint: JsonSerializer.Serialize(plan.Checkpoint),
                SafeError: result.SafeError, FailureCategory: FailureCategory.Unknown);
        if (result.RetryAt is not null)
            return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: JsonSerializer.Serialize(plan.Checkpoint),
                SafeError: result.SafeError, RetryAt: result.RetryAt, FailureCategory: Classify(result.SafeError));
        return Reject(result.SafeError ?? "youtube_publish_rejected", Classify(result.SafeError), AttentionState(result.SafeError));
    }

    private ProviderStep Step(ProviderPublication input, string operation, StepEffect effect, ReplaySafety replay, YouTubeCheckpoint state)
    {
        var plan = new YouTubePlan(input.Publication.AccountId, operation, input.Content.MediaAssets[0], input.Content.Title!,
            input.Content.Text ?? string.Empty, input.Target.Visibility, ParseOptions(input.Target.CanonicalOptionsJson), state);
        var opaque = JsonSerializer.Serialize(plan);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(opaque)));
        return new($"youtube.{operation}.v1", effect, replay, digest, timeProvider.GetUtcNow(), OpaquePlan: opaque);
    }

    private static YouTubeOptions ParseOptions(string canonicalOptionsJson)
    {
        try
        {
            using var options = JsonDocument.Parse(canonicalOptionsJson);
            if (options.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("YouTube options must be a JSON object.");
            var madeForKids = false;
            bool? containsSyntheticMedia = null;
            foreach (var property in options.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "madeForKids":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("YouTube madeForKids must be boolean.");
                        madeForKids = property.Value.GetBoolean();
                        break;
                    case "containsSyntheticMedia":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("YouTube containsSyntheticMedia must be boolean.");
                        containsSyntheticMedia = property.Value.GetBoolean();
                        break;
                    case "uploadNoticeAcknowledged":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("YouTube uploadNoticeAcknowledged must be boolean.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown YouTube option '{property.Name}'.");
                }
            }
            return new(madeForKids, containsSyntheticMedia);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("YouTube options JSON is invalid.", ex);
        }
    }

    private static bool TryCheckpoint(string? value, out YouTubeCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(value)) { checkpoint = new(); return true; }
        try
        {
            checkpoint = JsonSerializer.Deserialize<YouTubeCheckpoint>(value) ?? new();
            return checkpoint.Version == 1 && checkpoint.NextOffset >= 0;
        }
        catch (JsonException) { checkpoint = new(); return false; }
    }

    private static YouTubeStatusSnapshot Snapshot(YouTubeVideoStatus status) => new(
        status.PrivacyStatus ?? "private", status.Embeddable, status.License, status.PublicStatsViewable,
        status.SelfDeclaredMadeForKids, status.ContainsSyntheticMedia);

    private StepResult Continue(
        YouTubeCheckpoint checkpoint,
        JobKind nextJobKind,
        DateTimeOffset? dueAt = null,
        PublicationState? observedState = null) =>
        new(StepOutcome.Pending, EffectCertainty.Confirmed, Checkpoint: JsonSerializer.Serialize(checkpoint),
            RetryAt: dueAt ?? timeProvider.GetUtcNow(), ObservedState: observedState, NextJobKind: nextJobKind);

    private static StepResult ProviderFailure(YouTubeProviderException ex) => ex.Retryable
        ? new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: ex.SafeCode, FailureCategory: Classify(ex.SafeCode))
        : Reject(ex.SafeCode, Classify(ex.SafeCode), AttentionState(ex.SafeCode));

    private static StepResult Reject(string safeError, FailureCategory category, PublicationState? observedState = null) =>
        new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: safeError, ObservedState: observedState, FailureCategory: category);

    private static PublicationState? AttentionState(string? safeCode) => safeCode switch
    {
        "youtube_auth_or_scope_rejected" or "youtube_reconnect_required" or "auth_required" or "credential_unavailable" or
        "youtube_quota_exceeded" or "youtube_upload_limit_exceeded" or "youtube_policy_rejected" or
        "youtube_channel_suspended" or "youtube_forbidden" => PublicationState.NeedsAttention,
        _ => null,
    };

    private static string KnownReason(string prefix, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return prefix;
        var safe = new string(reason.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());
        return safe.Length == 0 ? prefix : $"{prefix}_{safe}";
    }

    private static FailureCategory Classify(string? safeCode) => safeCode switch
    {
        "youtube_rate_limited" or "youtube_quota_exceeded" or "youtube_upload_limit_exceeded" => FailureCategory.RateLimit,
        "youtube_network_unavailable" or "youtube_temporary_unavailable" or "youtube_upload_session_expired" or
            "youtube_upload_response_uncertain" or "youtube_upload_no_progress" => FailureCategory.Network,
        "youtube_auth_or_scope_rejected" or "youtube_token_rejected" or "youtube_reconnect_required" or
            "auth_required" or "credential_unavailable" => FailureCategory.Authentication,
        "youtube_invalid_input" or "media_integrity_mismatch" or "media_unavailable" or
            "youtube_upload_offset_invalid" or "youtube_checkpoint_invalid" => FailureCategory.InvalidInput,
        "youtube_publish_malformed_success" or "youtube_publish_server_uncertain" or "youtube_publish_not_observed" => FailureCategory.Unknown,
        _ => FailureCategory.Provider,
    };
}
