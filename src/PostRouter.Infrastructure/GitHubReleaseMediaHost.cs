using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed record GitHubReleaseMediaHostOptions(
    string Owner, string Repository, string ReleaseTag,
    TimeSpan? DefaultLifetime = null, long MaximumBytes = 2L * 1024 * 1024 * 1024);

// The token is supplied at the request boundary. The blob ID is not a token.
public interface IGitHubMediaTokenSource
{
    Task<byte[]> GetTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class VaultGitHubMediaTokenSource(IVault vault, string blobId) : IGitHubMediaTokenSource
{
    public const string Purpose = GitHubMediaConfigurationService.CredentialPurpose;

    public Task<byte[]> GetTokenAsync(CancellationToken cancellationToken = default) =>
        vault.GetAsync(blobId, Purpose, cancellationToken);
}

public sealed class GitHubReleaseMediaHost : ITemporaryPublicMediaHost, IDisposable
{
    private const string HostKind = "github-release-asset";
    private const int ContractVersion = 1;
    private const int MaximumPages = 100;
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly Uri UploadRoot = new("https://uploads.github.com/");
    private readonly GitHubReleaseMediaHostOptions _options;
    private readonly IGitHubMediaTokenSource _tokens;
    private readonly HttpClient _api;
    private readonly HttpClient _upload;
    private readonly TimeProvider _time;
    private readonly bool _ownsClients;

    private GitHubReleaseMediaHost(GitHubReleaseMediaHostOptions options, IGitHubMediaTokenSource tokens,
        HttpClient api, HttpClient upload, TimeProvider time, bool ownsClients)
    {
        ValidateOptions(options);
        _options = options;
        _tokens = tokens;
        _api = api;
        _upload = upload;
        _time = time;
        _ownsClients = ownsClients;
    }

    // Production clients never follow redirects with an Authorization header.
    // HTTP client replacement is internal and used only by tests with fake handlers.
    public static GitHubReleaseMediaHost CreateProduction(GitHubReleaseMediaHostOptions options, IGitHubMediaTokenSource tokens) =>
        new(options, tokens, NewClient(), NewClient(), TimeProvider.System, true);

    public async Task CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var path = $"repos/{_options.Owner}/{_options.Repository}";
        using (var request = NewRequest(HttpMethod.Get, new Uri(ApiRoot, path)))
        using (var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode != HttpStatusCode.OK) throw Failure(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var repository = document.RootElement;
            if (Text(repository, "full_name") is not { } name
                || !string.Equals(name, $"{_options.Owner}/{_options.Repository}", StringComparison.OrdinalIgnoreCase)
                || !repository.TryGetProperty("private", out var privateProperty)
                || privateProperty.ValueKind != JsonValueKind.False)
                throw Safe("github_repository_not_public");
        }
        _ = await GetReleaseIdAsync(cancellationToken).ConfigureAwait(false);
    }

    internal GitHubReleaseMediaHost(GitHubReleaseMediaHostOptions options, IGitHubMediaTokenSource tokens,
        HttpClient api, HttpClient upload, TimeProvider time)
        : this(options, tokens, api, upload, time, false) { }

    public PublicMediaStagingOperation Prepare(MediaAsset asset, DateTimeOffset? expiresAt = null)
    {
        var extension = Extension(asset.DetectedMime);
        ValidateSource(asset.Sha256, asset.SizeBytes);
        var now = _time.GetUtcNow();
        var expiry = expiresAt ?? now.Add(_options.DefaultLifetime ?? TimeSpan.FromHours(24));
        if (expiry <= now) throw Safe("media_expiry_invalid");
        var payload = new OperationPayload(HostKind, ContractVersion, _options.Owner, _options.Repository,
            _options.ReleaseTag, $"{Guid.NewGuid():N}{extension}", asset.Sha256.ToLowerInvariant(),
            asset.SizeBytes, asset.DetectedMime, now, expiry);
        return new PublicMediaStagingOperation(Encode(payload));
    }

    public async Task<StagedPublicAsset> StageAsync(MediaAsset asset, PublicMediaStagingOperation operation,
        CancellationToken cancellationToken = default)
    {
        var prepared = ReadOperation(operation);
        if (!string.Equals(asset.Sha256, prepared.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || asset.SizeBytes != prepared.SourceSizeBytes || asset.DetectedMime != prepared.MimeType)
            throw Safe("media_operation_source_mismatch");

        await using var source = await VerifyAndOpenAsync(asset, cancellationToken).ConfigureAwait(false);
        var releaseId = await GetReleaseIdAsync(cancellationToken).ConfigureAwait(false);
        var existing = await FindAssetAsync(prepared, releaseId, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        var path = $"repos/{_options.Owner}/{_options.Repository}/releases/{releaseId}/assets?name={prepared.AssetName}";
        using var request = NewRequest(HttpMethod.Post, new Uri(UploadRoot, path));
        request.Content = new StreamContent(source);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(prepared.MimeType);
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(_upload, request, cancellationToken).ConfigureAwait(false);
        }
        catch (TemporaryPublicMediaException error) when (error.Code is "github_timeout" or "github_network_error")
        {
            return await RecoverAfterAmbiguousUploadAsync(prepared, releaseId, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.Created)
            {
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity || (int)response.StatusCode >= 500)
                    return await RecoverAfterAmbiguousUploadAsync(prepared, releaseId, cancellationToken).ConfigureAwait(false);
                throw Failure(response);
            }
            try
            {
                using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                return FromAsset(document.RootElement, prepared, releaseId);
            }
            catch (TemporaryPublicMediaException error) when (error.Code is "github_timeout" or "github_network_error")
            {
                return await RecoverAfterAmbiguousUploadAsync(prepared, releaseId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Read-only recovery: never initiates another upload after an ambiguous result or crash.
    public async Task<StagedPublicAsset?> RecoverAsync(PublicMediaStagingOperation operation, CancellationToken cancellationToken = default)
    {
        var prepared = ReadOperation(operation);
        var releaseId = await GetReleaseIdAsync(cancellationToken).ConfigureAwait(false);
        return await FindAssetAsync(prepared, releaseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(StagedPublicAssetHandle handle, CancellationToken cancellationToken = default)
    {
        var staged = ReadHandle(handle);
        var releaseId = await GetReleaseIdAsync(cancellationToken).ConfigureAwait(false);
        if (releaseId != staged.ReleaseId) throw Safe("github_release_changed");
        var path = $"repos/{_options.Owner}/{_options.Repository}/releases/assets/{staged.AssetId}";
        using (var request = NewRequest(HttpMethod.Get, new Uri(ApiRoot, path)))
        using (var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            if (response.StatusCode != HttpStatusCode.OK) throw Failure(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var remote = document.RootElement;
            if (Number(remote, "id") != staged.AssetId || Text(remote, "name") != staged.AssetName)
                throw Safe("github_asset_identity_mismatch");
        }
        using var delete = NewRequest(HttpMethod.Delete, new Uri(ApiRoot, path));
        using var deleted = await SendAsync(_api, delete, cancellationToken).ConfigureAwait(false);
        if (deleted.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
        throw Failure(deleted);
    }

    private async Task<StagedPublicAsset> RecoverAfterAmbiguousUploadAsync(OperationPayload operation, long releaseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var recovered = await FindAssetAsync(operation, releaseId, cancellationToken).ConfigureAwait(false);
            if (recovered is not null) return recovered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TemporaryPublicMediaException) { }
        throw Safe("github_upload_outcome_unknown");
    }

    private async Task<long> GetReleaseIdAsync(CancellationToken cancellationToken)
    {
        var path = $"repos/{_options.Owner}/{_options.Repository}/releases/tags/{_options.ReleaseTag}";
        using var request = NewRequest(HttpMethod.Get, new Uri(ApiRoot, path));
        using var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Failure(response);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var release = document.RootElement;
        var id = Number(release, "id");
        if (id <= 0 || Text(release, "tag_name") != _options.ReleaseTag
            || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False)
            throw Safe("github_release_invalid");
        return id;
    }

    private async Task<StagedPublicAsset?> FindAssetAsync(OperationPayload operation, long releaseId,
        CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaximumPages; page++)
        {
            var path = $"repos/{_options.Owner}/{_options.Repository}/releases/{releaseId}/assets?per_page=100&page={page}";
            using var request = NewRequest(HttpMethod.Get, new Uri(ApiRoot, path));
            using var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw Failure(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Safe("github_invalid_response");
            var assets = document.RootElement;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name is null) throw Safe("github_invalid_response");
                if (name == operation.AssetName) return FromAsset(asset, operation, releaseId);
            }
            if (assets.GetArrayLength() < 100) return null;
        }
        throw Safe("github_asset_list_incomplete");
    }

    private StagedPublicAsset FromAsset(JsonElement asset, OperationPayload operation, long releaseId)
    {
        var id = Number(asset, "id");
        var name = Text(asset, "name");
        var urlText = Text(asset, "browser_download_url");
        if (id <= 0 || name != operation.AssetName || urlText is null
            || Text(asset, "state") != "uploaded") throw Safe("github_invalid_response");
        if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var actualSize) || actualSize != operation.SourceSizeBytes)
            throw Safe("github_asset_source_mismatch");
        var digest = Text(asset, "digest");
        if (digest is not null && !string.Equals(digest, "sha256:" + operation.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw Safe("github_asset_source_mismatch");
        var expected = new Uri($"https://github.com/{_options.Owner}/{_options.Repository}/releases/download/{_options.ReleaseTag}/{name}");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var publicUrl)
            || !string.Equals(publicUrl.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal))
            throw Safe("github_public_url_invalid");
        var handle = new HandlePayload(HostKind, ContractVersion, _options.Owner, _options.Repository,
            _options.ReleaseTag, releaseId, id, name, publicUrl.AbsoluteUri, operation.SourceSha256,
            operation.SourceSizeBytes, operation.CreatedAt, operation.ExpiresAt);
        return new StagedPublicAsset(publicUrl, new StagedPublicAssetHandle(Encode(handle)),
            operation.CreatedAt, operation.ExpiresAt, operation.SourceSha256, operation.SourceSizeBytes);
    }

    private async Task<FileStream> VerifyAndOpenAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(asset.StorageRef)) throw Safe("media_missing");
            var stream = new FileStream(asset.StorageRef, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                if (stream.Length != asset.SizeBytes || stream.Length <= 0 || stream.Length > _options.MaximumBytes)
                    throw Safe("media_size_mismatch");
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw Safe("media_sha256_mismatch");
                stream.Position = 0;
                return stream;
            }
            catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (IOException) { throw Safe("media_read_failed"); }
        catch (UnauthorizedAccessException) { throw Safe("media_read_failed"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Any(value => value is < 33 or > 126)) throw Safe("github_credential_invalid");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(bytes));
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Safe("github_timeout"); }
        catch (HttpRequestException) { throw Safe("github_network_error"); }
        catch (TemporaryPublicMediaException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { throw Safe("github_credential_unavailable"); }
        finally
        {
            request.Headers.Authorization = null;
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("post-router-media-staging/1");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    { Timeout = TimeSpan.FromHours(2) };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw Safe("github_invalid_response"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Safe("github_timeout"); }
        catch (HttpRequestException) { throw Safe("github_network_error"); }
        catch (IOException) { throw Safe("github_network_error"); }
    }

    private static TemporaryPublicMediaException Failure(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400) return Safe("github_redirect_rejected");
        if (response.StatusCode == HttpStatusCode.Unauthorized) return Safe("github_unauthorized");
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
                && (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")
                    || response.Headers.RetryAfter is not null))) return Safe("github_rate_limited");
        if (response.StatusCode == HttpStatusCode.Forbidden) return Safe("github_forbidden");
        if ((int)response.StatusCode >= 500) return Safe("github_server_error");
        return Safe("github_request_rejected");
    }

    private OperationPayload ReadOperation(PublicMediaStagingOperation operation)
    {
        var value = Decode<OperationPayload>(operation.Value);
        if (value.Host != HostKind || value.Version != ContractVersion || !SameRepository(value.Owner, value.Repository, value.Tag)
            || !ValidAssetName(value.AssetName, value.MimeType) || value.ExpiresAt <= value.CreatedAt)
            throw Safe("media_operation_invalid");
        ValidateSource(value.SourceSha256, value.SourceSizeBytes);
        return value;
    }

    private HandlePayload ReadHandle(StagedPublicAssetHandle handle)
    {
        var value = Decode<HandlePayload>(handle.Value);
        if (value.Host != HostKind || value.Version != ContractVersion || !SameRepository(value.Owner, value.Repository, value.Tag)
            || value.ReleaseId <= 0 || value.AssetId <= 0 || !ValidAssetName(value.AssetName, null)
            || value.ExpiresAt <= value.CreatedAt) throw Safe("media_handle_invalid");
        ValidateSource(value.SourceSha256, value.SourceSizeBytes);
        var expected = $"https://github.com/{_options.Owner}/{_options.Repository}/releases/download/{_options.ReleaseTag}/{value.AssetName}";
        if (value.PublicUrl != expected) throw Safe("media_handle_invalid");
        return value;
    }

    private bool SameRepository(string owner, string repository, string tag) =>
        string.Equals(owner, _options.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(repository, _options.Repository, StringComparison.OrdinalIgnoreCase)
        && string.Equals(tag, _options.ReleaseTag, StringComparison.Ordinal);

    private static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));

    private static T Decode<T>(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw Safe("media_handle_invalid");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value)) ?? throw Safe("media_handle_invalid"); }
        catch (FormatException) { throw Safe("media_handle_invalid"); }
        catch (JsonException) { throw Safe("media_handle_invalid"); }
    }

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ValidateOptions(GitHubReleaseMediaHostOptions options)
    {
        if (!ValidSegment(options.Owner) || !ValidSegment(options.Repository) || !ValidSegment(options.ReleaseTag)
            || options.MaximumBytes <= 0 || options.MaximumBytes > 2L * 1024 * 1024 * 1024
            || (options.DefaultLifetime is { } lifetime && lifetime <= TimeSpan.Zero))
            throw Safe("github_staging_config_invalid");
    }

    private static bool ValidSegment(string? value) =>
        value is { Length: > 0 and <= 100 } && value[0] != '.' && value[^1] != '.'
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void ValidateSource(string? sha256, long size)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || size <= 0)
            throw Safe("media_metadata_invalid");
    }

    private static string Extension(string mime) => mime switch
    {
        "video/mp4" => ".mp4",
        "image/jpeg" => ".jpg",
        _ => throw Safe("media_type_unsupported"),
    };

    private static bool ValidAssetName(string? name, string? mime) =>
        name is { Length: 36 } && Guid.TryParseExact(name[..32], "N", out _)
        && (mime is null ? name[32..] is ".mp4" or ".jpg" : name[32..] == Extension(mime));

    private static TemporaryPublicMediaException Safe(string code) => new(code);

    public void Dispose()
    {
        if (_ownsClients) { _api.Dispose(); _upload.Dispose(); }
    }

    private sealed record OperationPayload(string Host, int Version, string Owner, string Repository, string Tag,
        string AssetName, string SourceSha256, long SourceSizeBytes, string MimeType,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private sealed record HandlePayload(string Host, int Version, string Owner, string Repository, string Tag,
        long ReleaseId, long AssetId, string AssetName, string PublicUrl, string SourceSha256,
        long SourceSizeBytes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
}
