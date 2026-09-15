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
    private const int MaximumRequestBodyBytes = 64 * 1024;
    private static readonly string[] AssetNames = ["index.html", "app.css", "app.js"];

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
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
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

        app.MapPost("/api/posts", async (CreateTextPostRequest request, CancellationToken token) =>
            Results.Json(await runtime.Operations.EnqueueTextAsync(request, token).ConfigureAwait(false)));
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

        app.MapPost("/api/accounts/connect", (ConnectRequest request, HttpContext context) =>
        {
            var callback = CallbackUri(context.Request);
            var session = runtime.Accounts.BeginConnect(request.Provider, request.ClientId, callback, request.Alias);
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5));
            return Results.Json(new { authorizationUrl = session.AuthorizationUri.AbsoluteUri, expiresInSeconds = 300 });
        });
        app.MapPost("/api/accounts/{accountId:guid}/reconnect", async (Guid accountId, HttpContext context, CancellationToken token) =>
        {
            var session = await runtime.Accounts.BeginReconnectAsync(accountId, CallbackUri(context.Request), token).ConfigureAwait(false);
            RemoveExpired(authorizations);
            authorizations[session.State] = new(session, DateTimeOffset.UtcNow.AddMinutes(5));
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
                return Results.Content(OAuthPage(false), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
            try
            {
                _ = await runtime.Accounts.CompleteConnectAsync(pending.Session, code, state, token).ConfigureAwait(false);
                return Results.Content(OAuthPage(true), "text/html; charset=utf-8");
            }
            catch
            {
                return Results.Content(OAuthPage(false), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
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

    private static string OAuthPage(bool success) => success
        ? "<!doctype html><html lang=en><meta charset=utf-8><title>post-router</title><body><h1>Connected</h1><p>The account is connected. You may close this window.</p></body></html>"
        : "<!doctype html><html lang=en><meta charset=utf-8><title>post-router</title><body><h1>Connection failed</h1><p>Return to post-router and start the connection again.</p></body></html>";

    private sealed record PendingAuthorization(AuthorizationSession Session, DateTimeOffset ExpiresAt);
    private sealed record ConnectRequest(string Provider, string ClientId, string? Alias);
    private sealed record GuiError(string Code, string Message);
}
