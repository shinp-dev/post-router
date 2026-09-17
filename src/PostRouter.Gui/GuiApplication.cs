using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostRouter.Application;
using PostRouter.Infrastructure;

namespace PostRouter.Gui;

public sealed record GuiOptions(string? DataDirectory = null, int Port = 43127, bool OpenBrowser = true);

public sealed class GuiHost(WebApplication application, PostRouterRuntime runtime, Uri address) : IAsyncDisposable
{
    public Uri Address { get; } = address;
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => application.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync().ConfigureAwait(false);
        await application.DisposeAsync().ConfigureAwait(false);
        await runtime.DisposeAsync().ConfigureAwait(false);
    }
}

public static class GuiApplication
{
    private const int MaximumJsonRequestBodyBytes = 64 * 1024;
    private const long MaximumImageRequestBodyBytes = 21L * 1024 * 1024;
    private const long MaximumImageBytes = 5L * 1024 * 1024;
    private const long MaximumVideoBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumVideoRequestBodyBytes = MaximumVideoBytes + 1024 * 1024;
    private static readonly string[] AssetNames = ["index.html", "app.css", "app.js"];
    private static readonly HashSet<string> OAuthStateErrorCodes = new(StringComparer.Ordinal)
    {
        "oauth_state_mismatch",
        "oauth_scope_missing",
        "oauth_refresh_token_missing",
        "reconnect_account_mismatch",
        "auth_required",
    };

    public static async Task<GuiHost> StartAsync(GuiOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "GUI port must be between 0 and 65535.");
        var runtime = await RuntimeFactory.CreateAsync(options.DataDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(GuiApplication).Assembly.FullName,
                Args = [],
            });
            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.Configure<FormOptions>(form =>
            {
                form.MultipartBodyLengthLimit = MaximumVideoRequestBodyBytes;
                form.ValueLengthLimit = MaximumJsonRequestBodyBytes;
            });
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestBodySize = MaximumVideoRequestBodyBytes;
                server.AddServerHeader = false;
                server.Listen(IPAddress.Loopback, options.Port);
            });
            var app = builder.Build();
            Configure(app, runtime);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = ResolveAddress(app);
            if (options.OpenBrowser)
            {
                try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return new GuiHost(app, runtime, address);
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void Configure(WebApplication app, PostRouterRuntime runtime)
    {
        var csrfToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var authorizations = new ConcurrentDictionary<string, PendingAuthorization>(StringComparer.Ordinal);

        app.Use(async (context, next) =>
        {
            AddSecurityHeaders(context.Response.Headers);
            if (!string.Equals(context.Request.Host.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new GuiError("invalid_host", "GUI is available only through its loopback address."));
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api") &&
                !IsAuthorizedMutation(context.Request, csrfToken))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new GuiError("request_rejected", "The local request could not be verified."));
                return;
            }
            var isMediaRequest = context.Request.Path.Equals(new PathString("/api/posts/images")) ||
                context.Request.Path.Equals(new PathString("/api/posts/video"));
            var isSecretFileRequest = context.Request.Path.Equals(new PathString("/api/accounts/connect/file")) ||
                context.Request.Path.Value?.EndsWith("/reconnect/file", StringComparison.Ordinal) == true;
            if (HttpMethods.IsPost(context.Request.Method) && !isMediaRequest && !isSecretFileRequest &&
                context.Request.ContentLength > MaximumJsonRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new GuiError("request_too_large", "The request is too large."));
                return;
            }
            try { await next(context).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (context.Response.HasStarted) throw;
                var failure = MapError(ex);
                context.Response.StatusCode = failure.StatusCode;
                await context.Response.WriteAsJsonAsync(new GuiError(failure.Code, failure.Message));
            }
        });

        app.MapGet("/", () => EmbeddedAsset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.css", () => EmbeddedAsset("app.css", "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => EmbeddedAsset("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/api/session", () => Results.Json(new { csrfToken }));
        app.MapGet("/api/dashboard", async (CancellationToken token) => Results.Json(await runtime.Operations.DashboardAsync(token).ConfigureAwait(false)));
        app.MapGet("/api/providers", () => Results.Json(runtime.Operations.Capabilities()));
        app.MapGet("/api/accounts", async (CancellationToken token) => Results.Json(await runtime.Operations.AccountsAsync(token).ConfigureAwait(false)));
        app.MapGet("/api/publications", async (int? limit, CancellationToken token) =>
            Results.Json(await runtime.Operations.PublicationsAsync(limit ?? 200, token).ConfigureAwait(false)));
        app.MapGet("/api/publications/{publicationId:guid}", async (Guid publicationId, CancellationToken token) =>
        {
            var detail = await runtime.Operations.PublicationAsync(publicationId, token).ConfigureAwait(false);
            return detail is null ? Results.NotFound(new GuiError("not_found", "Publication was not found.")) : Results.Json(detail);
        });
        app.MapGet("/api/publications/{publicationId:guid}/approval", async (Guid publicationId, CancellationToken token) =>
            Results.Json(await runtime.Operations.PublicationApprovalAsync(publicationId, token).ConfigureAwait(false)));

        app.MapPost("/api/posts", async (CreateTextPostRequest request, CancellationToken token) =>
            Results.Json(await runtime.Operations.EnqueueTextAsync(request, token).ConfigureAwait(false)));
        app.MapPost("/api/posts/images", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.ContentLength > MaximumImageRequestBodyBytes) throw new BadHttpRequestException("Image request is too large.");
            var form = await request.ReadFormAsync(token).ConfigureAwait(false);
            if (!Guid.TryParse(form["accountId"], out var accountId)) throw new ArgumentException("Account ID is invalid.");
            var text = form["text"].ToString();
            var clientRequestId = form["clientRequestId"].ToString();
            DateTimeOffset? publishAt = null;
            if (!string.IsNullOrWhiteSpace(form["publishAt"].ToString()))
            {
                if (!DateTimeOffset.TryParse(form["publishAt"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    throw new ArgumentException("Publish time is invalid.");
                publishAt = parsed;
            }
            if (form.Files.Count is < 1 or > 4) throw new ArgumentException("One to four images are required.");
            var incoming = Path.Combine(runtime.DataDirectory, "incoming");
            Directory.CreateDirectory(incoming);
            var temporary = new List<string>();
            try
            {
                var assets = new List<PostRouter.Domain.MediaAsset>();
                foreach (var file in form.Files)
                {
                    if (file.Length is <= 0 or > MaximumImageBytes) throw new ArgumentException("Each image must be between 1 byte and 5 MB.");
                    var path = Path.Combine(incoming, $"{Guid.NewGuid():N}.upload");
                    temporary.Add(path);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        await file.CopyToAsync(output, token).ConfigureAwait(false);
                    if (!await IsJpegAsync(path, token).ConfigureAwait(false))
                        throw new ArgumentException("Only JPEG images are supported.");
                    var asset = await runtime.Spool.ImportAsync(path, token).ConfigureAwait(false);
                    assets.Add(asset);
                }
                return Results.Json(await runtime.Operations.EnqueueImagesAsync(
                    new(accountId, text, assets, publishAt, clientRequestId), token).ConfigureAwait(false));
            }
            finally
            {
                foreach (var path in temporary)
                    try { File.Delete(path); } catch (IOException) { }
            }
        });
        app.MapPost("/api/posts/video", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.ContentLength > MaximumVideoRequestBodyBytes) throw new BadHttpRequestException("Video request is too large.");
            var form = await request.ReadFormAsync(token).ConfigureAwait(false);
            if (!Guid.TryParse(form["accountId"], out var accountId)) throw new ArgumentException("Account ID is invalid.");
            if (form.Files.Count != 1 || form.Files[0].Name != "video") throw new ArgumentException("One MP4 video is required.");
            if (!bool.TryParse(form["madeForKids"], out var madeForKids)) throw new ArgumentException("Made for Kids must be selected.");
            if (!bool.TryParse(form["containsSyntheticMedia"], out var synthetic)) throw new ArgumentException("Synthetic media selection is invalid.");
            if (!bool.TryParse(form["uploadNoticeAcknowledged"], out var acknowledged) || !acknowledged)
                throw new ArgumentException("YouTube upload notice must be acknowledged.");
            var file = form.Files[0];
            if (file.Length is <= 0 or > MaximumVideoBytes) throw new ArgumentException("Video size is outside the supported range.");
            var incoming = Path.Combine(runtime.DataDirectory, "incoming");
            Directory.CreateDirectory(incoming);
            var path = Path.Combine(incoming, $"{Guid.NewGuid():N}.upload");
            try
            {
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    await file.CopyToAsync(output, token).ConfigureAwait(false);
                var asset = await runtime.Spool.ImportAsync(path, token).ConfigureAwait(false);
                return Results.Json(await runtime.Operations.EnqueueVideoAsync(new(
                    accountId, form["title"].ToString(), form["description"].ToString(), asset,
                    madeForKids, synthetic, form["visibility"].ToString(), acknowledged,
                    form["clientRequestId"].ToString()), token).ConfigureAwait(false));
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        });
        app.MapPost("/api/posts/{postId:guid}/cancel", async (Guid postId, CancellationToken token) =>
        {
            var changed = await runtime.Operations.CancelAsync(postId, token).ConfigureAwait(false);
            return changed == 0
                ? Results.Conflict(new GuiError("invalid_state", "No cancellable publication was found."))
                : Results.Json(new { postId, changed });
        });
        app.MapPost("/api/publications/{publicationId:guid}/retry", async (Guid publicationId, CancellationToken token) =>
        {
            await runtime.Operations.RetryAsync(publicationId, token).ConfigureAwait(false);
            return Results.Json(new { publicationId, queued = true });
        });
        app.MapPost("/api/publications/{publicationId:guid}/reconcile", async (Guid publicationId, CancellationToken token) =>
        {
            await runtime.Operations.ReconcileAsync(publicationId, token).ConfigureAwait(false);
            return Results.Json(new { publicationId, queued = true, reposted = false });
        });
        app.MapPost("/api/publications/{publicationId:guid}/approve", async (Guid publicationId, CancellationToken token) =>
        {
            await runtime.Operations.ApproveAsync(publicationId, token).ConfigureAwait(false);
            return Results.Json(new { publicationId, approved = true });
        });

        app.MapPost("/api/accounts/connect", (ConnectRequest request, HttpContext context) =>
        {
            if (request.ClientSecret is not null && !string.Equals(request.Provider, "youtube", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Client secret is supported only for YouTube.");
            if (request.ClientSecret is not null && string.IsNullOrWhiteSpace(request.ClientSecret))
                throw new ArgumentException("Client secret must not be blank.");
            var callback = CallbackUri(context.Request);
            var session = runtime.Accounts.BeginConnect(request.Provider, request.ClientId, callback, request.Alias);
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5), request.ClientSecret);
            return Results.Json(new { authorizationUrl = session.AuthorizationUri.AbsoluteUri, expiresInSeconds = 300 });
        });
        app.MapPost("/api/accounts/connect/file", async (HttpContext context, CancellationToken token) =>
        {
            var form = await context.Request.ReadFormAsync(token).ConfigureAwait(false);
            var clientSecret = await ReadClientSecretFileAsync(form.Files, token).ConfigureAwait(false);
            var session = runtime.Accounts.BeginConnect(form["provider"].ToString(), form["clientId"].ToString(),
                CallbackUri(context.Request), form["alias"].ToString());
            if (session.Provider != "youtube") throw new ArgumentException("Client secret is supported only for YouTube.");
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5), clientSecret);
            return Results.Json(new { authorizationUrl = session.AuthorizationUri.AbsoluteUri, expiresInSeconds = 300 });
        });
        app.MapPost("/api/accounts/{accountId:guid}/reconnect", async (Guid accountId, ReconnectRequest request, HttpContext context, CancellationToken token) =>
        {
            var session = await runtime.Accounts.BeginReconnectAsync(accountId, CallbackUri(context.Request), token).ConfigureAwait(false);
            if (request.ClientSecret is not null && !string.Equals(session.Provider, "youtube", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Client secret is supported only for YouTube.");
            if (request.ClientSecret is not null && string.IsNullOrWhiteSpace(request.ClientSecret))
                throw new ArgumentException("Client secret must not be blank.");
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5), request.ClientSecret);
            return Results.Json(new { authorizationUrl = session.AuthorizationUri.AbsoluteUri, expiresInSeconds = 300 });
        });
        app.MapPost("/api/accounts/{accountId:guid}/reconnect/file", async (Guid accountId, HttpContext context, CancellationToken token) =>
        {
            var form = await context.Request.ReadFormAsync(token).ConfigureAwait(false);
            var clientSecret = await ReadClientSecretFileAsync(form.Files, token).ConfigureAwait(false);
            var session = await runtime.Accounts.BeginReconnectAsync(accountId, CallbackUri(context.Request), token).ConfigureAwait(false);
            if (session.Provider != "youtube") throw new ArgumentException("Client secret is supported only for YouTube.");
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5), clientSecret);
            return Results.Json(new { authorizationUrl = session.AuthorizationUri.AbsoluteUri, expiresInSeconds = 300 });
        });
        app.MapPost("/api/accounts/{accountId:guid}/disconnect", async (Guid accountId, CancellationToken token) =>
        {
            if (!await runtime.Accounts.DisconnectAsync(accountId, token).ConfigureAwait(false))
                return Results.NotFound(new GuiError("not_found", "Account was not found."));
            return Results.Json(new { accountId, status = "Disconnected", historyPreserved = true, queuePreserved = true });
        });
        app.MapPost("/api/accounts/{accountId:guid}/revoke", async (Guid accountId, CancellationToken token) =>
            Results.Json(await runtime.Accounts.RevokeAsync(accountId, token).ConfigureAwait(false)));

        app.MapGet("/oauth/callback", async (HttpContext context, CancellationToken token) =>
        {
            var state = context.Request.Query["state"].ToString();
            var code = context.Request.Query["code"].ToString();
            if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code) ||
                !authorizations.TryRemove(state, out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Content(OAuthFailurePage(null), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
            try
            {
                _ = await runtime.Accounts.CompleteConnectAsync(pending.Session, code, state, pending.ClientSecret, token).ConfigureAwait(false);
                return Results.Content(OAuthSuccessPage(), "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(OAuthFailureLog(ex));
                return Results.Content(OAuthFailurePage(ex), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }
        });
    }

    private static Uri ResolveAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var value = addresses?.SingleOrDefault(address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return value is null ? throw new InvalidOperationException("GUI loopback address was not assigned.") : new Uri(value);
    }

    private static IResult EmbeddedAsset(string fileName, string contentType)
    {
        if (!AssetNames.Contains(fileName, StringComparer.Ordinal)) return Results.NotFound();
        var name = $"PostRouter.Gui.Web.{fileName}";
        var stream = typeof(GuiApplication).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("GUI asset is unavailable.");
        return Results.Stream(stream, contentType);
    }

    private static bool IsAuthorizedMutation(HttpRequest request, string expectedToken)
    {
        var supplied = request.Headers["X-Post-Router-CSRF"].ToString();
        var origin = request.Headers.Origin.ToString();
        var expectedOrigin = $"{request.Scheme}://{request.Host.Value}";
        if (!string.Equals(origin, expectedOrigin, StringComparison.Ordinal)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static Uri CallbackUri(HttpRequest request) => new($"{request.Scheme}://{request.Host.Value}/oauth/callback");

    private static async Task<string> ReadClientSecretFileAsync(IFormFileCollection files, CancellationToken cancellationToken)
    {
        if (files.Count != 1 || files[0].Name != "clientSecretFile")
            throw new ArgumentException("One client secret file is required.");
        await using var stream = files[0].OpenReadStream();
        return await ClientSecretFile.ReadAsync(stream, files[0].Length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsJpegAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[3];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) == header.Length &&
            header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
    }

    private static void AddSecurityHeaders(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
    }

    private static void RemoveExpired(ConcurrentDictionary<string, PendingAuthorization> sessions)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions.Where(pair => pair.Value.ExpiresAt < now)) sessions.TryRemove(pair.Key, out _);
    }

    private static (int StatusCode, string Code, string Message) MapError(Exception exception) => exception switch
    {
        ArgumentException => (StatusCodes.Status400BadRequest, "invalid_input", "The submitted values are invalid."),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "invalid_input", "The request is malformed."),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "not_found", "The requested item was not found."),
        NotSupportedException => (StatusCodes.Status422UnprocessableEntity, "unsupported", "The selected provider does not support this operation."),
        ProviderOperationException provider => (provider.Retryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status422UnprocessableEntity, provider.SafeCode, "The provider rejected or could not complete the operation."),
        InvalidOperationException => (StatusCodes.Status409Conflict, "invalid_state", "The operation is not allowed in the current state."),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "access_denied", "The operation is not permitted."),
        IOException => (StatusCodes.Status503ServiceUnavailable, "local_io_failure", "A required local operation failed."),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "The operation could not be completed."),
    };

    internal static string OAuthErrorCode(Exception? exception) => exception switch
    {
        ProviderOperationException provider when IsSafeErrorCode(provider.SafeCode) => provider.SafeCode,
        InvalidOperationException invalid when OAuthStateErrorCodes.Contains(invalid.Message) => invalid.Message,
        _ => "connection_failed",
    };

    private static bool IsSafeErrorCode(string code) => code.Length is > 0 and <= 64 &&
        code[0] is >= 'a' and <= 'z' &&
        code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static string OAuthFailurePage(Exception? exception) =>
        $"<!doctype html><html lang=en><meta charset=utf-8><title>post-router</title><body><h1>Connection failed</h1><p>Error code: <code>{WebUtility.HtmlEncode(OAuthErrorCode(exception))}</code></p><p>The account was not connected. You may close this window.</p></body></html>";

    internal static string OAuthFailureLog(Exception exception) =>
        $"OAuth callback failed: {exception.GetType().Name} ({OAuthErrorCode(exception)}).";

    internal static string OAuthSuccessPage() =>
        "<!doctype html><html lang=en><meta charset=utf-8><title>post-router</title><body><h1>Connected</h1><p>The account is connected. You may close this window.</p></body></html>";

    private sealed record PendingAuthorization(AuthorizationSession Session, DateTimeOffset ExpiresAt, string? ClientSecret)
    {
        public override string ToString() => "[REDACTED PENDING AUTHORIZATION]";
    }
    private sealed record ConnectRequest(string Provider, string ClientId, string? Alias, string? ClientSecret)
    {
        public override string ToString() => "[REDACTED CONNECT REQUEST]";
    }
    private sealed record ReconnectRequest(string? ClientSecret)
    {
        public override string ToString() => "[REDACTED RECONNECT REQUEST]";
    }
    private sealed record GuiError(string Code, string Message);
}
