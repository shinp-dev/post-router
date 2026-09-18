using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

internal sealed class InstagramApiClient(HttpClient httpClient)
{
    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Suppress HttpRequestOut DiagnosticListener events, whose payload exposes HttpRequestMessage.RequestUri.
        ActivityHeadersPropagator = null,
    };

    public async Task<InstagramShortLivedToken> ExchangeCodeAsync(string appId, string appSecret, string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(appId), "client_id");
        content.Add(new StringContent(appSecret), "client_secret");
        content.Add(new StringContent("authorization_code"), "grant_type");
        content.Add(new StringContent(redirectUri.AbsoluteUri), "redirect_uri");
        content.Add(new StringContent(code), "code");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.instagram.com/oauth/access_token") { Content = content };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InstagramProviderException("instagram_token_rejected");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = FirstDataOrRoot(document.RootElement);
            var accessToken = ReadString(value, "access_token");
            var permissions = ReadString(value, "permissions");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(permissions))
                throw new InstagramProviderException("instagram_token_invalid");
            return new(accessToken, permissions);
        }
        catch (JsonException) { throw new InstagramProviderException("instagram_token_invalid"); }
    }

    public async Task<InstagramTokenResponse> ExchangeLongLivedAsync(string appSecret, string accessToken, CancellationToken cancellationToken)
    {
        // 限定的例外: Meta公式Instagram Login token exchange仕様はGET queryを要求する。
        // Fixed HTTPS host/path only; no redirect, request URI diagnostic event, or URL logging.
        EnsureUriRedaction();
        var uri = $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={Uri.EscapeDataString(appSecret)}&access_token={Uri.EscapeDataString(accessToken)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InstagramProviderException("instagram_long_lived_token_rejected");
        return await ReadTokenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstagramTokenResponse> RefreshAsync(string accessToken, CancellationToken cancellationToken)
    {
        // The same Meta-documented GET-query exception applies only to this token lifecycle endpoint.
        EnsureUriRedaction();
        var uri = $"https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(accessToken)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InstagramProviderException("instagram_refresh_rejected");
        return await ReadTokenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstagramIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.instagram.com/me?fields=id%2Cuser_id%2Cusername");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InstagramProviderException("instagram_identity_rejected");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = FirstDataOrRoot(document.RootElement);
            var id = ReadString(value, "user_id") ?? ReadString(value, "id");
            var username = ReadString(value, "username");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(username))
                throw new InstagramProviderException("instagram_identity_invalid");
            return new(id, username);
        }
        catch (JsonException) { throw new InstagramProviderException("instagram_identity_invalid"); }
    }

    private static async Task<InstagramTokenResponse> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = FirstDataOrRoot(document.RootElement);
            var accessToken = ReadString(value, "access_token");
            var expiresIn = value.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt64(out var seconds) ? seconds : 0;
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InstagramProviderException("instagram_token_invalid");
            return new(accessToken, expiresIn);
        }
        catch (JsonException) { throw new InstagramProviderException("instagram_token_invalid"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new InstagramProviderException("instagram_transport_failure"); }
        catch (IOException) { throw new InstagramProviderException("instagram_transport_failure"); }
        catch (InvalidOperationException) { throw new InstagramProviderException("instagram_transport_failure"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstagramProviderException("instagram_transport_timeout");
        }
    }

    private static void EnsureUriRedaction()
    {
        // .NET 10 EventSource redacts query strings by default. Refuse the request if a host opts out.
        if (AppContext.TryGetSwitch("System.Net.Http.DisableUriRedaction", out var disabled) && disabled)
            throw new InstagramProviderException("instagram_uri_redaction_required");
        var setting = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION");
        if (string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase) || setting == "1")
            throw new InstagramProviderException("instagram_uri_redaction_required");
    }

    private static JsonElement FirstDataOrRoot(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
        data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : root;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}

internal sealed record InstagramShortLivedToken(string AccessToken, string Permissions)
{
    public override string ToString() => "[REDACTED INSTAGRAM TOKEN]";
}
internal sealed record InstagramTokenResponse(string AccessToken, long ExpiresIn)
{
    public override string ToString() => "[REDACTED INSTAGRAM TOKEN]";
}
internal sealed record InstagramIdentity(string Id, string Username);
internal sealed class InstagramProviderException(string safeCode) : ProviderOperationException(safeCode, false);

internal sealed class InstagramAuthProvider(InstagramApiClient client, TimeProvider timeProvider) : IInteractiveAuthProvider
{
    public const string RedirectUrl = "https://auth.shinp-studio.com/instagram/callback";
    public const string RequiredScope = "instagram_business_basic,instagram_business_content_publish";
    public string ProviderKey => "instagram";

    public AuthorizationSession BeginAuthorization(string clientId, Uri redirectUri, Guid? expectedAccountId = null,
        string? expectedSubject = null, string? requestedAlias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (!string.Equals(redirectUri.AbsoluteUri, RedirectUrl, StringComparison.Ordinal))
            throw new ArgumentException("Instagram redirect URI must match the registered callback.");
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUrl,
            ["response_type"] = "code",
            ["scope"] = RequiredScope,
            ["state"] = state,
        };
        var encoded = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new(ProviderKey, clientId, redirectUri, new Uri($"https://www.instagram.com/oauth/authorize?{encoded}"),
            state, "", RequiredScope, expectedAccountId, expectedSubject, requestedAlias);
    }

    public async Task<ConnectedIdentity> CompleteAuthorizationAsync(AuthorizationSession session, string code,
        string returnedState, string? clientSecret, CancellationToken cancellationToken)
    {
        var expected = Encoding.ASCII.GetBytes(session.State);
        var actual = Encoding.ASCII.GetBytes(returnedState);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidOperationException("oauth_state_mismatch");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("oauth_code_missing");
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new InvalidOperationException("instagram_app_secret_missing");
        var shortToken = await client.ExchangeCodeAsync(session.ClientId, clientSecret, code, session.RedirectUri, cancellationToken).ConfigureAwait(false);
        var granted = shortToken.Permissions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!RequiredScope.Split(',').All(scope => granted.Contains(scope, StringComparer.Ordinal)))
            throw new InvalidOperationException("oauth_scope_missing");
        var longToken = await client.ExchangeLongLivedAsync(clientSecret, shortToken.AccessToken, cancellationToken).ConfigureAwait(false);
        if (longToken.ExpiresIn <= 0) throw new InstagramProviderException("instagram_token_invalid");
        var identity = await client.GetIdentityAsync(longToken.AccessToken, cancellationToken).ConfigureAwait(false);
        return new(identity.Id, identity.Username, session.Scope,
            new TokenMaterial(longToken.AccessToken, null, timeProvider.GetUtcNow().AddSeconds(longToken.ExpiresIn), clientSecret));
    }

    public async Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.AccessToken)) throw new InstagramProviderException("instagram_reconnect_required");
        var refreshed = await client.RefreshAsync(current.AccessToken, cancellationToken).ConfigureAwait(false);
        if (refreshed.ExpiresIn <= 0) throw new InstagramProviderException("instagram_refresh_invalid");
        return new(new TokenMaterial(refreshed.AccessToken, null,
            timeProvider.GetUtcNow().AddSeconds(refreshed.ExpiresIn), current.ClientSecret), "instagram-token-refresh");
    }

    public Task RevokeAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken) =>
        throw new NotSupportedException("instagram_remote_revoke_unavailable");
}

internal sealed class InstagramConnectionOnlyAdapter : IProviderAdapter
{
    public string ProviderKey => "instagram";
    public ProviderCapabilities Capabilities { get; } = new("instagram", Array.Empty<ContentKind>(), [], "instagram-connection-only", 1, "{}", true, false);
    public bool RequiresConnectedAccount => true;
    public void Validate(Content content, TargetIntent target) => throw new NotSupportedException("instagram_publishing_unavailable");
    public Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken) =>
        throw new NotSupportedException("instagram_publishing_unavailable");
    public Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken) =>
        throw new NotSupportedException("instagram_publishing_unavailable");
    public Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken) =>
        throw new NotSupportedException("instagram_publishing_unavailable");
}
