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

internal sealed class XProviderException(string safeCode, bool retryable, Exception? inner = null) : ProviderOperationException(safeCode, retryable, inner);

internal sealed class XApiClient(HttpClient httpClient, TimeProvider timeProvider)
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly Uri ProductionBaseUri = new("https://api.x.com/");
    private readonly HttpClient _http = httpClient;

    public async Task<XTokenResponse> ExchangeCodeAsync(string clientId, Uri redirectUri, string code, string verifier, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier,
        };
        return await SendTokenAsync(fields, cancellationToken).ConfigureAwait(false);
    }

    public Task<XTokenResponse> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
        };
        return SendTokenAsync(fields, cancellationToken);
    }

    public async Task RevokeAsync(string clientId, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("2/oauth2/revoke"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["client_id"] = clientId }),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapSafeFailure(response.StatusCode, "x_revoke_rejected");
    }

    public async Task<XUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, Endpoint("2/users/me?user.fields=name,username"), accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapSafeFailure(response.StatusCode, "x_identity_rejected");
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<XUserEnvelope>(bytes);
            if (value?.Data is null || string.IsNullOrWhiteSpace(value.Data.Id) || !value.Data.Id.All(char.IsAsciiDigit))
                throw new XProviderException("x_identity_malformed", false);
            return value.Data;
        }
        catch (JsonException ex) { throw new XProviderException("x_identity_malformed", false, ex); }
    }

    public async Task<string> UploadImageAsync(string accessToken, XUploadAsset asset, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, Endpoint("2/media/upload"), accessToken);
        using var form = new MultipartFormDataContent();
        await using var stream = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != asset.SizeBytes) throw new XProviderException("media_integrity_mismatch", false);
        var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualHash, asset.Sha256, StringComparison.Ordinal))
            throw new XProviderException("media_integrity_mismatch", false);
        stream.Position = 0;
        using var media = new StreamContent(stream);
        media.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(media, "media", "image.jpg");
        form.Add(new StringContent("tweet_image"), "media_category");
        request.Content = form;
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapSafeFailure(response.StatusCode, "x_media_upload_rejected");
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<XMediaEnvelope>(bytes);
            if (string.IsNullOrWhiteSpace(value?.Data?.Id)) throw new XProviderException("x_media_upload_malformed", false);
            return value.Data.Id;
        }
        catch (JsonException ex) { throw new XProviderException("x_media_upload_malformed", false, ex); }
    }

    public async Task<XCreatePostResult> CreateTextPostAsync(string accessToken, string text, IReadOnlyList<string>? mediaIds, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, Endpoint("2/tweets"), accessToken);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new XCreatePostRequest(
            text, mediaIds is { Count: > 0 } ? new XCreatePostMedia(mediaIds) : null)));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (XProviderException ex) when (ex.Retryable)
        {
            return new(false, null, ex.SafeCode, null, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, null, "x_publish_response_lost", null, true);
        }
        using (response)
        {
            var retryAt = RetryAt(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(false, null, "x_rate_limited", retryAt ?? timeProvider.GetUtcNow().AddMinutes(15), false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, null, "x_auth_or_scope_rejected", null, false);
            if ((int)response.StatusCode is >= 400 and < 500)
                return new(false, null, "x_post_rejected", null, false);
            if (!response.IsSuccessStatusCode)
                return new(false, null, "x_publish_ambiguous_server_error", retryAt, true);
            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            try
            {
                var envelope = JsonSerializer.Deserialize<XPostEnvelope>(bytes);
                var id = envelope?.Data?.Id;
                if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiDigit))
                    return new(false, null, "x_publish_malformed_success", null, true);
                return new(true, id, null, null, false);
            }
            catch (JsonException) { return new(false, null, "x_publish_malformed_success", null, true); }
        }
    }

    private async Task<XTokenResponse> SendTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("2/oauth2/token")) { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapSafeFailure(response.StatusCode, "x_token_rejected");
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<XTokenResponse>(bytes);
            if (value is null || string.IsNullOrWhiteSpace(value.AccessToken) || value.ExpiresIn <= 0)
                throw new XProviderException("x_token_malformed", false);
            return value;
        }
        catch (JsonException ex) { throw new XProviderException("x_token_malformed", false, ex); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new XProviderException("x_network_unavailable", true, ex); }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Uri Endpoint(string relative)
    {
        var baseUri = _http.BaseAddress ?? ProductionBaseUri;
        return new Uri(baseUri, relative);
    }

    private static XProviderException MapSafeFailure(HttpStatusCode status, string fallback) => status switch
    {
        HttpStatusCode.TooManyRequests => new("x_rate_limited", true),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new("x_auth_or_scope_rejected", false),
        _ when (int)status >= 500 => new("x_temporary_unavailable", true),
        _ => new(fallback, false),
    };

    private DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return timeProvider.GetUtcNow().Add(delta);
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        if (response.Headers.TryGetValues("x-rate-limit-reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new XProviderException("x_response_too_large", false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes) throw new XProviderException("x_response_too_large", false);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record XCreatePostRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("media")] XCreatePostMedia? Media);
    private sealed record XCreatePostMedia([property: JsonPropertyName("media_ids")] IReadOnlyList<string> MediaIds);
    private sealed record XMediaEnvelope([property: JsonPropertyName("data")] XMedia? Data);
    private sealed record XMedia([property: JsonPropertyName("id")] string Id);
    private sealed record XPostEnvelope([property: JsonPropertyName("data")] XPost? Data);
    private sealed record XPost([property: JsonPropertyName("id")] string Id);
    private sealed record XUserEnvelope([property: JsonPropertyName("data")] XUser? Data);
}

internal sealed record XTokenResponse(
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

internal sealed record XUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("username")] string? Username);

internal sealed record XCreatePostResult(bool Success, string? PostId, string? SafeError, DateTimeOffset? RetryAt, bool Ambiguous);
internal sealed record XUploadAsset(string Path, string Sha256, long SizeBytes);

internal sealed class XAuthProvider(XApiClient client, TimeProvider timeProvider) : IInteractiveAuthProvider
{
    public const string RequiredScope = "tweet.read tweet.write users.read offline.access";
    public string ProviderKey => "x";

    public AuthorizationSession BeginAuthorization(string clientId, Uri redirectUri, Guid? expectedAccountId = null, string? expectedSubject = null, string? requestedAlias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ValidateLoopbackRedirect(redirectUri);
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
        };
        var encoded = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new(ProviderKey, clientId, redirectUri, new Uri($"https://x.com/i/oauth2/authorize?{encoded}"), state, verifier,
            RequiredScope, expectedAccountId, expectedSubject, requestedAlias);
    }

    public async Task<ConnectedIdentity> CompleteAuthorizationAsync(AuthorizationSession session, string code, string returnedState, CancellationToken cancellationToken)
    {
        if (!FixedEquals(session.State, returnedState)) throw new InvalidOperationException("oauth_state_mismatch");
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var token = await client.ExchangeCodeAsync(session.ClientId, session.RedirectUri, code, session.CodeVerifier, cancellationToken).ConfigureAwait(false);
        var identity = await client.GetCurrentUserAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
        var scope = string.IsNullOrWhiteSpace(token.Scope) ? session.Scope : token.Scope;
        if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tweet.write", StringComparer.Ordinal))
            throw new InvalidOperationException("oauth_scope_missing");
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw new InvalidOperationException("oauth_refresh_token_missing");
        var material = new TokenMaterial(token.AccessToken, token.RefreshToken, timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn));
        return new(identity.Id, identity.Username ?? identity.Name ?? identity.Id, scope, material);
    }

    public async Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant.ClientId) || string.IsNullOrWhiteSpace(current.RefreshToken))
            throw new XProviderException("x_reconnect_required", false);
        var token = await client.RefreshAsync(grant.ClientId, current.RefreshToken, cancellationToken).ConfigureAwait(false);
        var material = new TokenMaterial(token.AccessToken, token.RefreshToken ?? current.RefreshToken, timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn));
        return new(material, "x-token-refresh");
    }

    public Task RevokeAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant.ClientId)) throw new XProviderException("x_revoke_client_missing", false);
        return client.RevokeAsync(grant.ClientId, current.RefreshToken ?? current.AccessToken, cancellationToken);
    }

    private static void ValidateLoopbackRedirect(Uri redirectUri)
    {
        var loopback = string.Equals(redirectUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(redirectUri.Host, out var address) && IPAddress.IsLoopback(address));
        if (!redirectUri.IsAbsoluteUri || !string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !loopback)
            throw new ArgumentException("X Native App redirect URI must use an explicit http loopback address.");
        if (redirectUri.IsDefaultPort) throw new ArgumentException("X redirect URI must include the exact registered loopback port.");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

internal sealed class XProviderAdapter(AuthCoordinator auth, XApiClient client, TimeProvider timeProvider) : IProviderAdapter
{
    public string ProviderKey => "x";
    public ProviderCapabilities Capabilities { get; } = new(
        "x", [ContentKind.TextOnly, ContentKind.ImageSet], ["public"], "x-options/v1", 1, "{}", true, true);
    public bool RequiresConnectedAccount => true;

    public void Validate(Content content, TargetIntent target)
    {
        if (content.Kind is not ContentKind.TextOnly and not ContentKind.ImageSet)
            throw new NotSupportedException("X supports text-only and JPEG image posts in this phase.");
        if (content.Kind == ContentKind.TextOnly && content.MediaAssets.Count != 0)
            throw new ArgumentException("Text-only X posts cannot contain media.");
        if (content.Kind == ContentKind.ImageSet && (content.MediaAssets.Count is < 1 or > 4 ||
            content.MediaAssets.Any(asset => asset.DetectedMime != "image/jpeg" || asset.SizeBytes > 5L * 1024 * 1024)))
            throw new ArgumentException("X image posts require one to four JPEG files, each at most 5 MB.");
        if (!string.Equals(target.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Phase 2A X supports public visibility only.");
        if (!string.Equals(target.OptionsSchema, "x-options/v1", StringComparison.Ordinal) || target.OptionsVersion != 1)
            throw new ArgumentException("Phase 2A X requires x-options/v1 options version 1.");
        using var options = JsonDocument.Parse(target.CanonicalOptionsJson);
        if (options.RootElement.EnumerateObject().Any()) throw new ArgumentException("Phase 2A X options must be an empty object.");
        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text.EnumerateRunes().Count() > 280)
            throw new ArgumentException("X text must contain 1 to 280 Unicode scalar values and no NUL character.");
    }

    public Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken)
    {
        Validate(input.Content, input.Target);
        var mediaState = ParseCheckpoint(checkpoint);
        var remaining = input.Content.MediaAssets.Skip(mediaState.MediaIds.Count).FirstOrDefault();
        var plan = remaining is not null
            ? JsonSerializer.Serialize(new XPublishPlan(input.Publication.AccountId, input.Content.Text!, "upload-image",
                new XUploadAsset(remaining.StorageRef, remaining.Sha256, remaining.SizeBytes), mediaState.MediaIds))
            : JsonSerializer.Serialize(new XPublishPlan(input.Publication.AccountId, input.Content.Text!, "create-post", null, mediaState.MediaIds));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan)));
        var uploading = remaining is not null;
        return Task.FromResult(new ProviderStep(uploading ? $"x.upload-image.{mediaState.MediaIds.Count}.v1" : "x.create-post.v2",
            uploading ? StepEffect.UploadOnly : StepEffect.MayPublish,
            uploading ? ReplaySafety.SafeRepeatNoPublication : ReplaySafety.NotReplayable, digest,
            timeProvider.GetUtcNow(), OpaquePlan: plan));
    }

    public async Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        XPublishPlan plan;
        try { plan = JsonSerializer.Deserialize<XPublishPlan>(providerStep.OpaquePlan ?? string.Empty) ?? throw new JsonException(); }
        catch (JsonException) { return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "x_plan_invalid", FailureCategory: FailureCategory.InvalidInput); }
        TokenMaterial token;
        try { token = await auth.GetValidTokenAsync(plan.AccountId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (XProviderException ex)
        {
            return ex.Retryable
                ? new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: ex.SafeCode, FailureCategory: Classify(ex.SafeCode))
                : new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: ex.SafeCode, ObservedState: PublicationState.NeedsAttention, FailureCategory: Classify(ex.SafeCode));
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "auth_required", StringComparison.Ordinal))
        {
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "auth_required", ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.Authentication);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or KeyNotFoundException)
        {
            return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "credential_unavailable", ObservedState: PublicationState.NeedsAttention, FailureCategory: FailureCategory.Authentication);
        }

        if (plan.Operation == "upload-image")
        {
            if (plan.Media is null || !File.Exists(plan.Media.Path))
                return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: "media_unavailable", FailureCategory: FailureCategory.InvalidInput);
            try
            {
                var mediaId = await client.UploadImageAsync(token.AccessToken, plan.Media, cancellationToken).ConfigureAwait(false);
                var next = new XMediaCheckpoint([.. plan.MediaIds, mediaId]);
                return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, Checkpoint: JsonSerializer.Serialize(next), RetryAt: timeProvider.GetUtcNow());
            }
            catch (XProviderException ex)
            {
                return ex.Retryable
                    ? new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: ex.SafeCode, FailureCategory: Classify(ex.SafeCode))
                    : new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: ex.SafeCode, FailureCategory: Classify(ex.SafeCode));
            }
        }

        var result = await client.CreateTextPostAsync(token.AccessToken, plan.Text, plan.MediaIds, cancellationToken).ConfigureAwait(false);
        if (result.Success) return new(StepOutcome.Completed, EffectCertainty.Confirmed, result.PostId, ObservedState: PublicationState.Published);
        if (result.Ambiguous) return new(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: result.SafeError, FailureCategory: Classify(result.SafeError));
        if (result.RetryAt is not null) return new(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: result.SafeError, RetryAt: result.RetryAt, FailureCategory: Classify(result.SafeError));
        var observed = string.Equals(result.SafeError, "x_auth_or_scope_rejected", StringComparison.Ordinal)
            ? PublicationState.NeedsAttention
            : (PublicationState?)null;
        return new(StepOutcome.Rejected, EffectCertainty.NoSideEffect, SafeError: result.SafeError, ObservedState: observed, FailureCategory: Classify(result.SafeError));
    }

    public Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken) =>
        Task.FromResult(new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: "x_reconcile_inconclusive", RetryAt: timeProvider.GetUtcNow().AddMinutes(15), FailureCategory: FailureCategory.Unknown));

    private static FailureCategory Classify(string? safeCode) => safeCode switch
    {
        "x_rate_limited" => FailureCategory.RateLimit,
        "x_network_unavailable" or "x_publish_response_lost" => FailureCategory.Network,
        "x_auth_or_scope_rejected" or "x_token_rejected" or "x_reconnect_required" or
            "auth_required" or "credential_unavailable" => FailureCategory.Authentication,
        "x_plan_invalid" or "media_integrity_mismatch" or "media_unavailable" => FailureCategory.InvalidInput,
        "x_publish_ambiguous_server_error" or "x_publish_malformed_success" or
            "x_reconcile_inconclusive" => FailureCategory.Unknown,
        _ => FailureCategory.Provider,
    };

    private static XMediaCheckpoint ParseCheckpoint(string? checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint)) return new([]);
        try { return JsonSerializer.Deserialize<XMediaCheckpoint>(checkpoint) ?? new([]); }
        catch (JsonException) { throw new InvalidDataException("X media checkpoint is malformed."); }
    }

    private sealed record XPublishPlan(Guid AccountId, string Text, string Operation, XUploadAsset? Media, IReadOnlyList<string> MediaIds);
    private sealed record XMediaCheckpoint(IReadOnlyList<string> MediaIds);
}
