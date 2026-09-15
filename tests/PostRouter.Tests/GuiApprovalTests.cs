using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PostRouter.Domain;
using PostRouter.Gui;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class GuiApprovalTests
{
    [Fact]
    public async Task Gui_exposes_and_approves_common_publication_gate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-gui-approval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
        var previousKey = Environment.GetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY");
        var key = new byte[32];
        Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
        Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", Convert.ToBase64String(key));

        PostRouterRuntime? seedRuntime = null;
        GuiHost? host = null;
        HttpClient? client = null;
        try
        {
            seedRuntime = await RuntimeFactory.CreateAsync(directory, new InMemoryMasterKeyStore(key));
            var accountId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var intent = new CanonicalPostIntent(
                "gui-approval",
                new Content(Guid.NewGuid(), ContentKind.TextOnly, "approval test", null, []),
                [new TargetIntent(accountId, "fake", "public", "fake-options/v1", 1, "{}", "fake-approval", ApprovalPolicy.RequireApproval)],
                new ScheduleIntent(ScheduleMode.Immediate, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)));
            var queued = await seedRuntime.Posts.EnqueueAsync(intent);
            var publicationId = Assert.Single(queued.PublicationIds);

            Assert.Equal(1, await seedRuntime.Worker.RunOnceAsync());
            Assert.Equal(PublicationState.AwaitingApproval,
                Assert.Single((await seedRuntime.Posts.GetAsync(queued.PostId))!.Publications).State);

            host = await GuiApplication.StartAsync(new GuiOptions(directory, 0, false));
            client = new HttpClient { BaseAddress = host.Address };
            using var session = JsonDocument.Parse(await client.GetStringAsync("/api/session"));
            var csrf = session.RootElement.GetProperty("csrfToken").GetString()!;

            using var approval = JsonDocument.Parse(await client.GetStringAsync($"/api/publications/{publicationId:D}/approval"));
            Assert.Equal("RequireApproval", approval.RootElement.GetProperty("policy").GetString());
            Assert.True(approval.RootElement.GetProperty("canApprove").GetBoolean());
            Assert.False(approval.RootElement.GetProperty("approved").GetBoolean());

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/publications/{publicationId:D}/approve")
            {
                Content = JsonContent.Create(new { }),
            };
            request.Headers.Add("Origin", client.BaseAddress.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-Post-Router-CSRF", csrf);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var approved = await seedRuntime.Operations.PublicationApprovalAsync(publicationId);
            Assert.True(approved.Approved);
            Assert.False(approved.CanApprove);

            Assert.Equal(1, await seedRuntime.Worker.RunOnceAsync());
            Assert.Equal(PublicationState.Published,
                Assert.Single((await seedRuntime.Posts.GetAsync(queued.PostId))!.Publications).State);
        }
        finally
        {
            client?.Dispose();
            if (host is not null) await host.DisposeAsync();
            if (seedRuntime is not null) await seedRuntime.DisposeAsync();
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", previousKey);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
