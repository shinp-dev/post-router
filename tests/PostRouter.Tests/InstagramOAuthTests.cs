using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class InstagramOAuthTests
{
    private static readonly Uri Redirect = new(InstagramAuthProvider.RedirectUrl);

    [Fact]
    public void Authorization_url_uses_registered_redirect_current_scopes_and_random_state_without_secrets()
    {
        using var http = new HttpClient(new Handler((_, _, _) => throw new NotImplementedException()));
        var provider = new InstagramAuthProvider(new InstagramApiClient(http), TimeProvider.System);
        var first = provider.BeginAuthorization("123456", Redirect);
        var second = provider.BeginAuthorization("123456", Redirect);

        Assert.Equal("www.instagram.com", first.AuthorizationUri.Host);
        Assert.Equal("/oauth/authorize", first.AuthorizationUri.AbsolutePath);
        Assert.Contains("instagram_business_basic", first.AuthorizationUri.Query);
        Assert.Contains("instagram_business_content_publish", first.AuthorizationUri.Query);
        Assert.Contains(Uri.EscapeDataString(InstagramAuthProvider.RedirectUrl), first.AuthorizationUri.Query);
        Assert.Contains("response_type=code", first.AuthorizationUri.Query);
        Assert.Contains($"state={first.State}", first.AuthorizationUri.Query);
        Assert.Equal(64, first.State.Length);
        Assert.NotEqual(first.State, second.State);
        Assert.DoesNotContain("client_secret", first.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("code_challenge", first.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => provider.BeginAuthorization("123456", new Uri("http://127.0.0.1:8765/instagram/callback")));
    }

    [Fact]
    public async Task Connection_exchanges_tokens_verifies_identity_and_vaults_secret()
    {
        const string secret = "instagram-secret-marker";
        await using var context = await TestContext.CreateAsync();
        using var http = new HttpClient(new Handler(async (request, call, token) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://api.instagram.com/oauth/access_token", request.RequestUri!.AbsoluteUri);
                Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
                var body = await request.Content.ReadAsStringAsync(token);
                Assert.Contains("instagram-secret-marker", body);
                Assert.Contains(InstagramAuthProvider.RedirectUrl, body);
                Assert.Contains("authorization-code-marker", body);
                return Json(HttpStatusCode.OK, "{\"data\":[{\"access_token\":\"instagram-short-marker\",\"permissions\":\"instagram_business_basic,instagram_business_content_publish\"}]}");
            }
            if (call == 2)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://graph.instagram.com/access_token", request.RequestUri!.GetLeftPart(UriPartial.Path));
                Assert.Contains("instagram-secret-marker", request.RequestUri.Query);
                Assert.Contains("instagram-short-marker", request.RequestUri.Query);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"instagram-long-marker\",\"expires_in\":5184000}");
            }
            Assert.Equal("/me", request.RequestUri?.AbsolutePath);
            Assert.Contains("user_id", request.RequestUri!.Query);
            Assert.Equal("instagram-long-marker", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, "{\"id\":\"app-scoped-id\",\"user_id\":\"professional-id\",\"username\":\"creator-name\"}");
        }));
        var provider = new InstagramAuthProvider(new InstagramApiClient(http), context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "instagram-grant-locks")), context.Gate, [provider], context.Time);
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate,
            new NoOpAccountOperationLockFactory(), [provider], auth);
        var session = accounts.BeginConnect("instagram", "123456", Redirect);

        var account = await accounts.CompleteConnectAsync(session, "authorization-code-marker", session.State, secret);

        Assert.Equal("professional-id", account.RemoteSubject);
        Assert.Equal("creator-name", account.DisplayName);
        Assert.Equal("Connected", account.Status);
        var grant = await context.Store.GetAuthGrantForAccountAsync(account.AccountId);
        Assert.NotNull(grant);
        var stored = await context.Store.GetAsync(grant.VaultBlobId, "auth-token");
        var material = JsonSerializer.Deserialize<TokenMaterial>(stored);
        Assert.Equal("instagram-long-marker", material?.AccessToken);
        Assert.Equal(secret, material?.ClientSecret);
        foreach (var databaseFile in Directory.EnumerateFiles(context.Directory, "post-router.db*"))
        {
            await using var database = new FileStream(databaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var databaseBytes = new MemoryStream();
            await database.CopyToAsync(databaseBytes);
            var plaintext = Encoding.UTF8.GetString(databaseBytes.ToArray());
            Assert.DoesNotContain(secret, plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain("instagram-long-marker", plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain("instagram-short-marker", plaintext, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(secret, material!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("instagram-long-marker", material.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_missing_or_token_failure_never_exposes_response()
    {
        using var missingScopeHttp = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"data\":[{\"access_token\":\"short-secret\",\"permissions\":\"instagram_business_basic\"}]}"))));
        var provider = new InstagramAuthProvider(new InstagramApiClient(missingScopeHttp), TimeProvider.System);
        var session = provider.BeginAuthorization("123456", Redirect);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteAuthorizationAsync(session, "code-secret", session.State, "app-secret", CancellationToken.None));
        Assert.Equal("oauth_scope_missing", missing.Message);
        using var failedHttp = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(HttpStatusCode.BadRequest,
            "{\"error_message\":\"app-secret code-secret raw-description\"}"))));
        var failedProvider = new InstagramAuthProvider(new InstagramApiClient(failedHttp), TimeProvider.System);
        var failedSession = failedProvider.BeginAuthorization("123456", Redirect);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() =>
            failedProvider.CompleteAuthorizationAsync(failedSession, "code-secret", failedSession.State, "app-secret", CancellationToken.None));
        Assert.Equal("instagram_code_exchange_bad_request", failure.SafeCode);
        Assert.DoesNotContain("app-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-description", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("short-secret", new InstagramShortLivedToken("short-secret", "permissions").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("long-secret", new InstagramTokenResponse("long-secret", 100).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("code", 400, "instagram_code_exchange_bad_request")]
    [InlineData("code", 401, "instagram_code_exchange_unauthorized")]
    [InlineData("long", 400, "instagram_long_token_exchange_bad_request")]
    [InlineData("long", 401, "instagram_long_token_exchange_unauthorized")]
    [InlineData("identity", 400, "instagram_identity_bad_request")]
    [InlineData("identity", 401, "instagram_identity_unauthorized")]
    [InlineData("refresh", 400, "instagram_refresh_bad_request")]
    [InlineData("refresh", 401, "instagram_refresh_unauthorized")]
    public async Task Meta_failures_are_classified_by_stage_and_status_without_response_text(string stage, int status, string safeCode)
    {
        const string secret = "private-app-secret-marker";
        const string token = "private-access-token-marker";
        const string response = "{\"error\":{\"message\":\"private-meta-response-marker\",\"code\":190}}";
        using var http = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json((HttpStatusCode)status, response))));
        var client = new InstagramApiClient(http);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() => stage switch
        {
            "code" => client.ExchangeCodeAsync("123456", secret, "private-code-marker", Redirect, CancellationToken.None),
            "long" => client.ExchangeLongLivedAsync(secret, token, CancellationToken.None),
            "identity" => client.GetIdentityAsync(token, CancellationToken.None),
            "refresh" => client.RefreshAsync(token, CancellationToken.None),
            _ => throw new InvalidOperationException(),
        });
        Assert.Equal(safeCode, failure.SafeCode);
        var visible = failure.ToString();
        Assert.DoesNotContain(secret, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(token, visible, StringComparison.Ordinal);
        Assert.DoesNotContain("private-code-marker", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("private-meta-response-marker", visible, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"data\":[{\"permissions\":\"instagram_business_basic\"}]}", "instagram_code_exchange_token_missing")]
    [InlineData("{\"data\":[{\"access_token\":\"private-token-marker\"}]}", "instagram_permissions_response_missing")]
    [InlineData("not-json", "instagram_code_exchange_response_invalid")]
    public async Task Code_exchange_response_missing_fields_has_precise_safe_code(string body, string safeCode)
    {
        using var http = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, body))));
        var client = new InstagramApiClient(http);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() =>
            client.ExchangeCodeAsync("123456", "private-secret", "private-code", Redirect, CancellationToken.None));
        Assert.Equal(safeCode, failure.SafeCode);
        Assert.DoesNotContain("private-", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"expires_in\":5184000}", "instagram_long_token_missing")]
    [InlineData("{\"access_token\":\"private-token-marker\"}", "instagram_long_token_expiry_invalid")]
    [InlineData("{\"access_token\":\"private-token-marker\",\"expires_in\":0}", "instagram_long_token_expiry_invalid")]
    [InlineData("not-json", "instagram_long_token_response_invalid")]
    public async Task Long_token_response_missing_fields_has_precise_safe_code(string body, string safeCode)
    {
        using var http = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, body))));
        var client = new InstagramApiClient(http);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() =>
            client.ExchangeLongLivedAsync("private-secret", "private-short-token", CancellationToken.None));
        Assert.Equal(safeCode, failure.SafeCode);
        Assert.DoesNotContain("private-", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alternate_structured_permissions_and_numeric_string_expiry_keep_required_scope_check()
    {
        using var http = new HttpClient(new Handler((_, call, _) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.OK, "{\"data\":{\"access_token\":\"private-short-token\",\"permissions\":[\"instagram_business_basic\",\"instagram_business_content_publish\"]}}")
            : Json(HttpStatusCode.OK, "{\"access_token\":\"private-long-token\",\"expires_in\":\"5184000\"}"))));
        var client = new InstagramApiClient(http);
        var shortToken = await client.ExchangeCodeAsync("123456", "private-secret", "private-code", Redirect, CancellationToken.None);
        Assert.Equal("instagram_business_basic,instagram_business_content_publish", shortToken.Permissions);
        var longToken = await client.ExchangeLongLivedAsync("private-secret", shortToken.AccessToken, CancellationToken.None);
        Assert.Equal(5184000, longToken.ExpiresIn);
    }

    [Theory]
    [InlineData("code", "instagram_code_exchange_transport_failure")]
    [InlineData("long", "instagram_long_token_exchange_transport_failure")]
    [InlineData("identity", "instagram_identity_transport_failure")]
    public async Task Transport_failures_identify_stage_without_exposing_request_uri(string stage, string safeCode)
    {
        using var http = new HttpClient(new Handler((request, _, _) =>
            throw new HttpRequestException($"private-uri={request.RequestUri}")));
        var client = new InstagramApiClient(http);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() => stage switch
        {
            "code" => client.ExchangeCodeAsync("123456", "private-secret", "private-code", Redirect, CancellationToken.None),
            "long" => client.ExchangeLongLivedAsync("private-secret", "private-token", CancellationToken.None),
            "identity" => client.GetIdentityAsync("private-token", CancellationToken.None),
            _ => throw new InvalidOperationException(),
        });
        Assert.Equal(safeCode, failure.SafeCode);
        Assert.DoesNotContain("private-", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_secret_stays_in_vault_and_status_exposes_presence_only()
    {
        await using var context = await TestContext.CreateAsync();
        var settingsStore = new FileInstagramOAuthConfigurationStore(context.Directory);
        var settings = new InstagramOAuthConfigurationService(settingsStore, context.Store);
        Assert.Null((await settings.StatusAsync()).AppId);
        await settings.SetAppIdAsync("123456");
        var secret = Encoding.UTF8.GetBytes("instagram-app-secret-marker");
        await settings.SetAppSecretAsync(secret);
        var status = await settings.StatusAsync();
        Assert.True(status.AppSecretConfigured);
        Assert.Equal(InstagramAuthProvider.RedirectUrl, status.RedirectUri);
        Assert.DoesNotContain("instagram-app-secret-marker", JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.Equal("instagram-app-secret-marker", await settings.ReadAppSecretAsync());
        Assert.DoesNotContain("instagram-app-secret-marker", await File.ReadAllTextAsync(Path.Combine(context.Directory, "instagram-oauth-settings.json")), StringComparison.Ordinal);
        await settings.ClearAppSecretAsync();
        Assert.False((await settings.StatusAsync()).AppSecretConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ReadAppSecretAsync());
    }

    [Fact]
    public async Task Reconnect_refresh_and_disconnect_rotate_then_delete_encrypted_credentials()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = new HttpClient(new Handler((request, call, _) =>
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/oauth/access_token" => Json(HttpStatusCode.OK,
                    $"{{\"data\":[{{\"access_token\":\"short-{call}\",\"permissions\":\"instagram_business_basic,instagram_business_content_publish\"}}]}}"),
                "/access_token" => Json(HttpStatusCode.OK,
                    $"{{\"access_token\":\"long-{call}\",\"expires_in\":5184000}}"),
                "/refresh_access_token" => Json(HttpStatusCode.OK,
                    "{\"access_token\":\"refreshed-token\",\"expires_in\":5184000}"),
                "/me" => Json(HttpStatusCode.OK, "{\"id\":\"same-id\",\"username\":\"same-user\"}"),
                _ => throw new InvalidOperationException("Unexpected endpoint"),
            };
            return Task.FromResult(response);
        }));
        var provider = new InstagramAuthProvider(new InstagramApiClient(http), context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "instagram-grant-locks")), context.Gate, [provider], context.Time);
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate,
            new NoOpAccountOperationLockFactory(), [provider], auth);
        var firstSession = accounts.BeginConnect("instagram", "123456", Redirect);
        var first = await accounts.CompleteConnectAsync(firstSession, "first-code", firstSession.State, "app-secret");
        var firstGrant = (await context.Store.GetAuthGrantForAccountAsync(first.AccountId))!;

        var reconnect = await accounts.BeginReconnectAsync(first.AccountId, Redirect);
        var second = await accounts.CompleteConnectAsync(reconnect, "second-code", reconnect.State);
        Assert.Equal(first.AccountId, second.AccountId);
        var secondGrant = (await context.Store.GetAuthGrantForAccountAsync(first.AccountId))!;
        Assert.NotEqual(firstGrant.VaultBlobId, secondGrant.VaultBlobId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Store.GetAsync(firstGrant.VaultBlobId, "auth-token"));
        var secondMaterial = JsonSerializer.Deserialize<TokenMaterial>(await context.Store.GetAsync(secondGrant.VaultBlobId, "auth-token"));
        Assert.Equal("app-secret", secondMaterial?.ClientSecret);

        context.Time.Advance(TimeSpan.FromDays(60) - TimeSpan.FromMinutes(2));
        var refreshed = await auth.GetValidTokenAsync(first.AccountId);
        Assert.Equal("refreshed-token", refreshed.AccessToken);
        Assert.Equal("app-secret", refreshed.ClientSecret);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Store.GetAsync(secondGrant.VaultBlobId, "auth-token"));
        var finalGrant = (await context.Store.GetAuthGrantForAccountAsync(first.AccountId))!;
        Assert.True(await accounts.DisconnectAsync(first.AccountId));
        Assert.Null(await context.Store.GetAuthGrantForAccountAsync(first.AccountId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Store.GetAsync(finalGrant.VaultBlobId, "auth-token"));
        Assert.Equal("Disconnected", (await accounts.StatusAsync(first.AccountId))?.Status);
    }

    [Fact]
    public async Task Callback_accepts_forwarded_host_once_and_returns_static_page()
    {
        using var http = new HttpClient(new Handler((_, _, _) => throw new NotImplementedException()));
        var session = new InstagramAuthProvider(new InstagramApiClient(http), TimeProvider.System).BeginAuthorization("123456", Redirect);
        await using var listener = new InstagramCallbackListener(0);
        var completions = 0;
        var receive = listener.ReceiveAsync(session, (code, state, _) =>
        {
            Interlocked.Increment(ref completions);
            Assert.Equal("code-marker", code);
            Assert.Equal(session.State, state);
            return Task.FromResult(new AccountConnection(Guid.NewGuid(), "instagram", "creator", "Connected", "remote-id", "creator", "123456", session.Scope, DateTimeOffset.UtcNow.AddDays(60)));
        });
        var response = await SendCallbackAsync(listener.Port, $"/instagram/callback?code=code-marker&state={session.State}", "auth.shinp-studio.com");
        var result = await receive;
        Assert.Contains("200 OK", response);
        Assert.Contains("Instagramとの接続が完了", response);
        Assert.DoesNotContain("code-marker", response);
        Assert.Equal("remote-id", result.RemoteSubject);
        Assert.Equal(1, completions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.ReceiveAsync(session, (_, _, _) => throw new NotImplementedException()));
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        });
    }

    [Theory]
    [InlineData("/instagram/callback?code=code&state=wrong", "oauth_state_mismatch")]
    [InlineData("/instagram/callback?state={state}", "oauth_code_missing")]
    [InlineData("/instagram/callback?error=access_denied&state={state}", "oauth_authorization_failed")]
    [InlineData("/other?code=code&state={state}", "oauth_callback_invalid")]
    [InlineData("/instagram/callback?code=a&code=b&state={state}", "oauth_callback_invalid")]
    public async Task Callback_rejects_invalid_requests_without_echoing_values(string target, string safeCode)
    {
        using var http = new HttpClient(new Handler((_, _, _) => throw new NotImplementedException()));
        var session = new InstagramAuthProvider(new InstagramApiClient(http), TimeProvider.System).BeginAuthorization("123456", Redirect);
        await using var listener = new InstagramCallbackListener(0);
        var receive = listener.ReceiveAsync(session, (_, _, _) => throw new InvalidOperationException("must-not-complete"));
        var response = await SendCallbackAsync(listener.Port, target.Replace("{state}", session.State, StringComparison.Ordinal), "auth.shinp-studio.com");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => receive);
        Assert.Equal(safeCode, failure.Message);
        Assert.Contains("400 Bad Request", response);
        Assert.DoesNotContain("code=", response, StringComparison.Ordinal);
        Assert.DoesNotContain(session.State, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_timeout_and_cancellation_close_listener()
    {
        using var http = new HttpClient(new Handler((_, _, _) => throw new NotImplementedException()));
        var session = new InstagramAuthProvider(new InstagramApiClient(http), TimeProvider.System).BeginAuthorization("123456", Redirect);
        await using (var listener = new InstagramCallbackListener(0, TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.ReceiveAsync(session, (_, _, _) => throw new NotImplementedException()));
        await using (var listener = new InstagramCallbackListener(0))
        {
            using var cancellation = new CancellationTokenSource();
            var receive = listener.ReceiveAsync(session, (_, _, _) => throw new NotImplementedException(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        }
    }

    [Fact]
    public void Production_handler_prevents_redirects_and_raw_request_diagnostics()
    {
        using var handler = InstagramApiClient.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.ActivityHeadersPropagator);
    }

    [Fact]
    public async Task Transport_errors_do_not_include_sensitive_request_uri_or_response()
    {
        using var http = new HttpClient(new Handler((request, _, _) =>
            throw new HttpRequestException($"secret-uri={request.RequestUri}")));
        var client = new InstagramApiClient(http);
        var failure = await Assert.ThrowsAsync<InstagramProviderException>(() =>
            client.ExchangeLongLivedAsync("app-secret-marker", "short-token-marker", CancellationToken.None));
        Assert.Equal("instagram_long_token_exchange_transport_failure", failure.SafeCode);
        Assert.DoesNotContain("app-secret-marker", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("short-token-marker", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_diagnostics_do_not_emit_query_values()
    {
        var marker = "instagram-diagnostic-secret-" + Guid.NewGuid().ToString("N");
        using var events = new HttpEvents();
        var diagnostics = new List<IDisposable>();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(listener =>
        {
            if (listener.Name == "HttpHandlerDiagnosticListener")
                diagnostics.Add(listener.Subscribe(new DiagnosticObserver(value =>
                {
                    if (value.Key.StartsWith("System.Net.Http.HttpRequestOut", StringComparison.Ordinal))
                        events.CaptureDiagnostic(value.Key);
                })));
        }));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var connection = await server.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var client = new HttpClient(InstagramApiClient.CreateProductionHandler());
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/diagnostic?credential={marker}");
        await serving;
        foreach (var disposable in diagnostics) disposable.Dispose();
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(events.RequestStartCount > 0);
        Assert.Equal(0, events.DiagnosticRequestCount);
        Assert.DoesNotContain(marker, events.Captured, StringComparison.Ordinal);
    }

    private static async Task<string> SendCallbackAsync(int port, string target, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _call;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, Interlocked.Increment(ref _call), cancellationToken);
    }

    private sealed class HttpEvents : EventListener
    {
        private readonly StringBuilder _captured = new();
        private int _requestStartCount;
        private int _diagnosticRequestCount;
        public string Captured => _captured.ToString();
        public int RequestStartCount => _requestStartCount;
        public int DiagnosticRequestCount => _diagnosticRequestCount;
        public void CaptureDiagnostic(string value) { Interlocked.Increment(ref _diagnosticRequestCount); _captured.AppendLine(value); }
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Net.Http") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "RequestStart" && eventData.Payload is not null)
            {
                Interlocked.Increment(ref _requestStartCount);
                foreach (var value in eventData.Payload) _captured.AppendLine(value?.ToString());
            }
        }
    }

    private sealed class ListenerObserver(Action<DiagnosticListener> next) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class DiagnosticObserver(Action<KeyValuePair<string, object?>> next) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
