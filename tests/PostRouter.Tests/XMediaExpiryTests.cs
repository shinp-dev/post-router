using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class XMediaExpiryTests
{
    [Fact]
    public async Task Expired_uploaded_media_is_reuploaded_before_post_creation()
    {
        await using var context = await TestContext.CreateAsync();
        var imagePath = Path.Combine(context.Directory, "expiry.jpg");
        byte[] image = [0xff, 0xd8, 0xff, 0x07];
        await File.WriteAllBytesAsync(imagePath, image);
        var uploads = 0;
        var creates = 0;
        using var http = Client(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/2/media/upload")
            {
                var current = Interlocked.Increment(ref uploads);
                var id = current == 1 ? "media-old" : "media-new";
                var ttl = current == 1 ? 30 : 86_400;
                return Json(HttpStatusCode.OK, $"{{\"data\":{{\"id\":\"{id}\",\"expires_after_secs\":{ttl}}}}}");
            }

            Assert.Equal("/2/tweets", request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref creates);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("media-new", body, StringComparison.Ordinal);
            Assert.DoesNotContain("media-old", body, StringComparison.Ordinal);
            return Json(HttpStatusCode.Created, "{\"data\":{\"id\":\"123\"}}");
        });
        var setup = await BuildAsync(context, http);
        var queued = await setup.Posts.EnqueueAsync(XImageIntent(context, setup.Account.AccountId, imagePath, image));

        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        context.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await setup.Worker.RunOnceAsync());
        Assert.Equal(1, await setup.Worker.RunOnceAsync());

        Assert.Equal(2, uploads);
        Assert.Equal(1, creates);
        Assert.Equal(PublicationState.Published, Assert.Single((await setup.Posts.GetAsync(queued.PostId))!.Publications).State);
        await setup.Worker.DisposeAsync();
    }

    private static async Task<XSetup> BuildAsync(TestContext context, HttpClient http)
    {
        var client = new XApiClient(http, context.Time);
        var authProvider = new XAuthProvider(client, context.Time);
        var auth = new AuthCoordinator(context.Store, context.Store,
            new FileAuthGrantLockFactory(Path.Combine(context.Directory, "expiry-auth-locks")),
            context.Gate, [authProvider], context.Time);
        var account = await SaveConnectionAsync(context);
        var adapter = new XProviderAdapter(auth, client, context.Time);
        var registry = new ProviderRegistry([adapter]);
        var posts = new PostService(context.Store, context.Gate, registry, context.Time);
        var worker = new WorkerService(context.Store, registry,
            new FileWorkerLockFactory(Path.Combine(context.Directory, "expiry-worker.lock")),
            context.Gate, context.Time, accountOperationLocks: new NoOpAccountOperationLockFactory());
        return new(account, posts, worker);
    }

    private static async Task<AccountConnection> SaveConnectionAsync(TestContext context)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial(
            "access-token", "refresh-token", context.Time.GetUtcNow().AddHours(1)));
        var blob = await context.Store.PutAsync("auth-token", bytes);
        return await context.Store.SaveConnectedAccountAsync(new(
            "x", "x-expiry", "remote-user", "X Expiry Test", "client-id",
            XAuthProvider.RequiredScope, context.Time.GetUtcNow().AddHours(1), blob));
    }

    private static CanonicalPostIntent XImageIntent(TestContext context, Guid accountId, string path, byte[] bytes)
    {
        var asset = new MediaAsset(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.LongLength, "image/jpeg", path);
        return new("image-expiry", new Content(Guid.NewGuid(), ContentKind.ImageSet, "caption", null, [asset]),
            [new TargetIntent(accountId, "x", "public", "x-options/v1", 1, "{}", "x-expiry")],
            new ScheduleIntent(ScheduleMode.Immediate, context.Time.GetUtcNow(), TimeSpan.FromMinutes(15)));
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(new StubHandler(response)) { BaseAddress = new Uri("http://127.0.0.1/") };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }

    private sealed record XSetup(AccountConnection Account, PostService Posts, WorkerService Worker);
}
