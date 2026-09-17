using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class YouTubeProviderTests
{
    [Fact]
    public async Task OAuth_uses_pkce_offline_access_and_binds_channel_identity()
    {
        await using var context = await TestContext.CreateAsync();
        using var http = Client(async (request, call, cancellationToken) =>
        {
            if (call == 1)
            {
                Assert.Equal("oauth2.googleapis.com", request.RequestUri?.Host);
                Assert.Equal("/token", request.RequestUri?.AbsolutePath);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("code_verifier=", form);
                Assert.DoesNotContain("client_secret=", form, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"yt-access\",\"expires_in\":3600,\"refresh_token\":\"yt-refresh\",\"scope\":\"https://www.googleapis.com/auth/youtube.force-ssl\",\"token_type\":\"Bearer\"}");
            }

            Assert.Equal("/youtube/v3/channels", request.RequestUri?.AbsolutePath);
            Assert.Contains("mine=true", request.RequestUri?.Query);
            Assert.Equal("yt-access", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"channel-123\",\"snippet\":{\"title\":\"Channel Name\"}}]}");
        });
        var client = new YouTubeApiClient(http, context.Time);
        var provider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "yt-auth-locks")), context.Gate, [provider], context.Time);
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate,
            new NoOpAccountOperationLockFactory(), [provider], auth);

        var session = accounts.BeginConnect("youtube", "desktop-client", new Uri("http://127.0.0.1:9876/callback"), "yt-main");
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUri.Query);
        Assert.Contains("access_type=offline", session.AuthorizationUri.Query);
        Assert.DoesNotContain(session.CodeVerifier, session.AuthorizationUri.AbsoluteUri);

        var connected = await accounts.CompleteConnectAsync(session, "authorization-code", session.State);
        Assert.Equal("channel-123", connected.RemoteSubject);
        Assert.Equal("Channel Name", connected.DisplayName);
        Assert.Equal("Connected", connected.Status);
        Assert.NotNull(await context.Store.GetAuthGrantForAccountAsync(connected.AccountId));
    }

    [Fact]
    public async Task OAuth_secret_is_vaulted_and_reused_for_reconnect_and_refresh_with_pkce()
    {
        const string clientSecret = "desktop-secret-for-test";
        await using var context = await TestContext.CreateAsync();
        var calls = 0;
        using var http = Client(async (request, call, cancellationToken) =>
        {
            calls = call;
            if (call is 1 or 3 or 5 or 6)
            {
                Assert.Equal("oauth2.googleapis.com", request.RequestUri?.Host);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("client_secret=desktop-secret-for-test", form, StringComparison.Ordinal);
                if (call == 5)
                {
                    Assert.Contains("grant_type=refresh_token", form, StringComparison.Ordinal);
                    Assert.DoesNotContain("code_verifier=", form, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains("grant_type=authorization_code", form, StringComparison.Ordinal);
                    Assert.Contains("code_verifier=", form, StringComparison.Ordinal);
                }
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"yt-access\",\"expires_in\":3600,\"refresh_token\":\"yt-refresh\",\"scope\":\"https://www.googleapis.com/auth/youtube.force-ssl\"}");
            }
            Assert.Equal("/youtube/v3/channels", request.RequestUri?.AbsolutePath);
            return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"channel-123\",\"snippet\":{\"title\":\"Channel Name\"}}]}");
        });
        var client = new YouTubeApiClient(http, context.Time);
        var provider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "yt-secret-auth-locks")), context.Gate, [provider], context.Time);
        var accounts = new AccountConnectionService(context.Store, context.Store, context.Gate,
            new NoOpAccountOperationLockFactory(), [provider], auth);

        var session = accounts.BeginConnect("youtube", "desktop-client", new Uri("http://127.0.0.1:9876/callback"), "yt-main");
        Assert.DoesNotContain(clientSecret, session.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUri.Query, StringComparison.Ordinal);
        var connected = await accounts.CompleteConnectAsync(session, "authorization-code", session.State, clientSecret);
        Assert.DoesNotContain(clientSecret, JsonSerializer.Serialize(connected), StringComparison.Ordinal);
        var grant = (await context.Store.GetAuthGrantForAccountAsync(connected.AccountId))!;
        var stored = await context.Store.GetAsync(grant.VaultBlobId, "auth-token");
        try { Assert.Equal(clientSecret, JsonSerializer.Deserialize<TokenMaterial>(stored)?.ClientSecret); }
        finally { CryptographicOperations.ZeroMemory(stored); }
        await using (var database = new SqliteConnection($"Data Source={Path.Combine(context.Directory, "post-router.db")}"))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT ciphertext FROM vault_blobs WHERE id=$id";
            command.Parameters.AddWithValue("$id", grant.VaultBlobId);
            var ciphertext = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
            Assert.DoesNotContain(clientSecret, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        }

        var reconnect = await accounts.BeginReconnectAsync(connected.AccountId, new Uri("http://127.0.0.1:9876/callback"));
        Assert.DoesNotContain(clientSecret, reconnect.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        var reconnected = await accounts.CompleteConnectAsync(reconnect, "second-code", reconnect.State);
        Assert.Equal(connected.AccountId, reconnected.AccountId);

        context.Time.Advance(TimeSpan.FromHours(2));
        grant = (await context.Store.GetAuthGrantForAccountAsync(connected.AccountId))!;
        _ = await auth.RefreshAsync(grant.Id);
        var refreshed = (await context.Store.GetAuthGrantForAccountAsync(connected.AccountId))!;
        var refreshedBytes = await context.Store.GetAsync(refreshed.VaultBlobId, "auth-token");
        try { Assert.Equal(clientSecret, JsonSerializer.Deserialize<TokenMaterial>(refreshedBytes)?.ClientSecret); }
        finally { CryptographicOperations.ZeroMemory(refreshedBytes); }

        Assert.True(await accounts.DisconnectAsync(connected.AccountId));
        var disconnectedReconnect = await accounts.BeginReconnectAsync(connected.AccountId, new Uri("http://127.0.0.1:9876/callback"));
        var restored = await accounts.CompleteConnectAsync(disconnectedReconnect, "third-code", disconnectedReconnect.State, clientSecret);
        Assert.Equal(connected.AccountId, restored.AccountId);
        var restoredGrant = (await context.Store.GetAuthGrantForAccountAsync(restored.AccountId))!;
        var restoredBytes = await context.Store.GetAsync(restoredGrant.VaultBlobId, "auth-token");
        try { Assert.Equal(clientSecret, JsonSerializer.Deserialize<TokenMaterial>(restoredBytes)?.ClientSecret); }
        finally { CryptographicOperations.ZeroMemory(restoredBytes); }
        Assert.Equal(7, calls);
    }

    [Fact]
    public async Task Refresh_without_client_secret_omits_optional_form_field()
    {
        using var http = Client(async (request, _, cancellationToken) =>
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("grant_type=refresh_token", form, StringComparison.Ordinal);
            Assert.DoesNotContain("client_secret=", form, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, "{\"access_token\":\"yt-access\",\"expires_in\":3600}");
        });
        var client = new YouTubeApiClient(http, TimeProvider.System);

        _ = await client.RefreshAsync("desktop-client", "refresh-token", CancellationToken.None);
    }

    [Fact]
    public void Existing_vault_material_without_client_secret_remains_readable()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        var material = JsonSerializer.Deserialize<TokenMaterial>(legacy);
        Assert.NotNull(material);
        Assert.Null(material.ClientSecret);
    }

    [Fact]
    public async Task Private_resumable_upload_processes_then_publishes_known_video_once()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "normal.mp4");
        var updateCalls = 0;
        using var http = Client(async (request, call, cancellationToken) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/upload/youtube/v3/videos", request.RequestUri?.AbsolutePath);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("\"privacyStatus\":\"private\"", body);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=session-1");
                return response;
            }
            if (call == 2)
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Contains("upload_id=session-1", request.RequestUri?.Query);
                Assert.Equal(media.Length, request.Content?.Headers.ContentLength);
                return Json(HttpStatusCode.OK, "{\"id\":\"video-1\",\"status\":{\"uploadStatus\":\"uploaded\",\"privacyStatus\":\"private\"}}");
            }
            if (call == 3)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Processed("video-1", "private");
            }

            Interlocked.Increment(ref updateCalls);
            Assert.Equal(HttpMethod.Put, request.Method);
            var update = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("\"privacyStatus\":\"public\"", update);
            Assert.Contains("\"embeddable\":true", update);
            return Json(HttpStatusCode.OK, "{\"id\":\"video-1\",\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"public\"}}");
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-normal", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());

        var saved = await setup.Posts.GetAsync(queued.PostId);
        Assert.Equal(PublicationState.Published, Assert.Single(saved!.Publications).State);
        Assert.Equal("video-1", Assert.Single(saved.RemoteObjects).ProviderObjectId);
        Assert.Equal(1, updateCalls);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Private_upload_confirms_remote_video_and_persists_id_before_processing_finishes()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "private.mp4");
        var starts = 0;
        var uploads = 0;
        var reads = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=private-session");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"private-video-id\"}"));
            }
            Interlocked.Increment(ref reads);
            Assert.Contains("id=private-video-id", request.RequestUri?.Query, StringComparison.Ordinal);
            return Task.FromResult(Processed("private-video-id", "private"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(PrivateIntent(context, setup.Account.AccountId, "private-finish", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        var uploaded = (await setup.Posts.GetAsync(queued.PostId))!;
        Assert.Equal(PublicationState.Processing, Assert.Single(uploaded.Publications).State);
        Assert.Equal("private-video-id", Assert.Single(uploaded.RemoteObjects).ProviderObjectId);
        await setup.Worker.DisposeAsync();

        var restarted = await BuildAsync(context, http);
        Assert.Equal(1, await restarted.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Ready, Assert.Single((await restarted.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, await restarted.Worker.RunOnceAsync());
        var finished = (await restarted.Posts.GetAsync(queued.PostId))!;
        Assert.Equal(PublicationState.Published, Assert.Single(finished.Publications).State);
        Assert.Null(Assert.Single(finished.Publications).SafeError);
        Assert.Equal("private-video-id", Assert.Single(finished.RemoteObjects).ProviderObjectId);
        Assert.Equal(0, await restarted.Worker.RunOnceAsync());
        Assert.Equal(1, starts);
        Assert.Equal(1, uploads);
        Assert.Equal(2, reads);
        await restarted.Worker.DisposeAsync();
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    public async Task Private_confirmation_rejects_unexpected_remote_visibility(string privacy)
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "private-mismatch.mp4");
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=mismatch-session");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put) return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"mismatch-id\"}"));
            return Task.FromResult(Processed("mismatch-id", privacy));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(PrivateIntent(context, setup.Account.AccountId, $"private-{privacy}", media.Path, media.Bytes));
        for (var run = 0; run < 4; run++) Assert.Equal(1, await setup.Worker.RunOnceAsync());
        var status = (await setup.Posts.GetAsync(queued.PostId))!;
        Assert.Equal(PublicationState.NeedsAttention, Assert.Single(status.Publications).State);
        Assert.Equal("mismatch-id", Assert.Single(status.RemoteObjects).ProviderObjectId);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Private_confirmation_with_required_approval_waits_at_boundary()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "private-approval.mp4");
        var reads = 0;
        using var http = Client((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=private-approval-session");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put) return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"private-approval-id\"}"));
            Interlocked.Increment(ref reads);
            return Task.FromResult(Processed("private-approval-id", "private"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(PrivateIntent(context, setup.Account.AccountId, "private-approval", media.Path, media.Bytes,
            ApprovalPolicy.RequireApproval));
        for (var run = 0; run < 3; run++) Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.AwaitingApproval, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, reads);
        await setup.Operations.ApproveAsync(Assert.Single(queued.PublicationIds));
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(2, reads);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Upload_response_loss_queries_session_and_resumes_without_new_session()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "resume.mp4", [1, 2, 3, 4, 5, 6]);
        var starts = 0;
        var uploads = 0;
        var statusQueries = 0;
        using var http = Client((request, call, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/upload/youtube/v3/videos")
            {
                Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=resume-session");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put && request.RequestUri?.Query.Contains("resume-session", StringComparison.Ordinal) == true)
            {
                if (request.Content?.Headers.ContentLength == 0)
                {
                    Interlocked.Increment(ref statusQueries);
                    var incomplete = new HttpResponseMessage((HttpStatusCode)308);
                    incomplete.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
                    return Task.FromResult(incomplete);
                }
                var uploadNo = Interlocked.Increment(ref uploads);
                if (uploadNo == 1) throw new HttpRequestException("lost upload response");
                Assert.Equal("bytes 2-5/6", request.Content?.Headers.ContentRange?.ToString());
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-resume\"}"));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Processed("video-resume", "private"));
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-resume\",\"status\":{\"privacyStatus\":\"public\"}}"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-resume", media.Path, media.Bytes));

        for (var run = 0; run < 6; run++)
        {
            context.Time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(1, await setup.Worker.RunOnceAsync());
        }

        Assert.Equal(1, starts);
        Assert.Equal(2, uploads);
        Assert.Equal(1, statusQueries);
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.DisposeAsync();
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    public async Task Approval_gate_blocks_only_final_visibility_update(string visibility)
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "approval.mp4");
        var updateCalls = 0;
        using var http = Client((request, call, _) =>
        {
            if (call == 1)
            {
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=approval-session");
                return Task.FromResult(response);
            }
            if (call == 2) return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-approval\"}"));
            if (call == 3) return Task.FromResult(Processed("video-approval", "private"));
            Interlocked.Increment(ref updateCalls);
            return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"id\":\"video-approval\",\"status\":{{\"privacyStatus\":\"{visibility}\"}}}}"));
        });
        var setup = await BuildAsync(context, http);
        var intent = Intent(context, setup.Account.AccountId, $"youtube-approval-{visibility}", media.Path, media.Bytes, ApprovalPolicy.RequireApproval);
        intent = intent with { Targets = [intent.Targets[0] with { Visibility = visibility }] };
        var queued = await setup.Posts.EnqueueAsync(intent);
        var publicationId = Assert.Single(queued.PublicationIds);

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(0, updateCalls);
        Assert.Equal(PublicationState.AwaitingApproval, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);

        await setup.Operations.ApproveAsync(publicationId);
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, updateCalls);
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Ambiguous_public_update_reconciles_by_known_video_id_without_reupload()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "reconcile.mp4");
        var starts = 0;
        var uploads = 0;
        var updates = 0;
        var reads = 0;
        using var http = Client((request, call, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref starts);
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=reconcile-session");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Put && request.RequestUri?.Query.Contains("reconcile-session", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"video-reconcile\"}"));
            }
            if (request.Method == HttpMethod.Get)
            {
                var read = Interlocked.Increment(ref reads);
                return Task.FromResult(read == 1 ? Processed("video-reconcile", "private") : Processed("video-reconcile", "public"));
            }
            Interlocked.Increment(ref updates);
            throw new HttpRequestException("visibility response lost");
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-reconcile", media.Path, media.Bytes));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Unknown, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(1, starts);
        Assert.Equal(1, uploads);
        Assert.Equal(1, updates);
        Assert.Equal(2, reads);
        await setup.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Mutated_video_is_rejected_before_any_upload_http()
    {
        await using var context = await TestContext.CreateAsync();
        var media = await VideoAsync(context, "mutated.mp4", [10, 20, 30, 40]);
        var calls = 0;
        using var http = Client((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(Intent(context, setup.Account.AccountId, "youtube-mutated", media.Path, media.Bytes));
        await File.WriteAllBytesAsync(media.Path, [10, 20, 30, 41]);

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        var publication = Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications);
        Assert.Equal(PublicationState.Failed, publication.State);
        Assert.Equal("media_integrity_mismatch", publication.SafeError);
        Assert.Equal(0, calls);
        await setup.Worker.DisposeAsync();
    }

    private static async Task<YouTubeSetup> BuildAsync(TestContext context, HttpClient http)
    {
        var client = new YouTubeApiClient(http, context.Time);
        var authProvider = new YouTubeAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "youtube-auth-locks")), context.Gate, [authProvider], context.Time);
        var account = await SaveConnectionAsync(context);
        var adapter = new YouTubeProviderAdapter(auth, client, context.Time);
        var registry = new ProviderRegistry([adapter]);
        var applicationStore = new ApprovalAwarePostRouterStore(context.Store, context.Approvals, context.Time);
        var posts = new PostService(applicationStore, context.Gate, registry, context.Time);
        var worker = new WorkerService(applicationStore, registry,
            new FileWorkerLockFactory(Path.Combine(context.Directory, "youtube-worker.lock")), context.Gate, context.Time,
            accountOperationLocks: new NoOpAccountOperationLockFactory());
        var operations = new OperationsService(applicationStore, context.Gate, registry, posts, context.Time, context.Approvals);
        return new(account, posts, worker, operations);
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context)
    {
        var expires = context.Time.GetUtcNow().AddHours(2);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("youtube-access", "youtube-refresh", expires));
        var blob = await context.Store.PutAsync("auth-token", bytes);
        return await context.Store.SaveConnectedAccountAsync(new(
            "youtube", "youtube-test", "channel-test", "YouTube Test", "desktop-client",
            YouTubeAuthProvider.RequiredScope, expires, blob));
    }

    private static CanonicalPostIntent Intent(
        TestContext context,
        Guid accountId,
        string key,
        string path,
        byte[] bytes,
        ApprovalPolicy approvalPolicy = ApprovalPolicy.Automatic)
    {
        var asset = new MediaAsset(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, "video/mp4", path);
        return new(key, new Content(Guid.NewGuid(), ContentKind.Video, "description", "Video title", [asset]),
            [new TargetIntent(accountId, "youtube", "public", "youtube-options/v1", 1, "{}", "youtube-test", approvalPolicy)],
            new ScheduleIntent(ScheduleMode.Immediate, context.Time.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    private static CanonicalPostIntent PrivateIntent(TestContext context, Guid accountId, string key, string path, byte[] bytes,
        ApprovalPolicy approvalPolicy = ApprovalPolicy.Automatic)
    {
        var source = Intent(context, accountId, key, path, bytes, approvalPolicy);
        return source with
        {
            Targets = [source.Targets[0] with
        {
            Visibility = "private",
            CanonicalOptionsJson = "{\"madeForKids\":false,\"uploadNoticeAcknowledged\":true}",
        }]
        };
    }

    private static async Task<(string Path, byte[] Bytes, long Length)> VideoAsync(TestContext context, string name, byte[]? bytes = null)
    {
        bytes ??= [1, 2, 3, 4];
        var path = Path.Combine(context.Directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, bytes, bytes.LongLength);
    }

    private static HttpResponseMessage Processed(string id, string privacy) => Json(HttpStatusCode.OK,
        $"{{\"items\":[{{\"id\":\"{id}\",\"status\":{{\"uploadStatus\":\"processed\",\"privacyStatus\":\"{privacy}\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true}},\"processingDetails\":{{\"processingStatus\":\"succeeded\"}}}}]}}");

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

    private sealed record YouTubeSetup(AccountConnection Account, PostService Posts, WorkerService Worker, OperationsService Operations);
}
