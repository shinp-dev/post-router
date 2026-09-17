using System.Text.Json;
using PostRouter.Application;
using PostRouter.Cli;
using PostRouter.Domain;
using PostRouter.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PostRouter.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task Connect_accepts_absolute_loopback_redirect_before_provider_lookup()
    {
        var result = await InvokeConnectAsync("unregistered", "http://127.0.0.1:8765/callback");
        Assert.NotEqual(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("unsupported", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Connect_rejects_malformed_redirect_as_invalid_input()
    {
        var result = await InvokeConnectAsync("youtube", "not-a-uri");
        Assert.NotEqual(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("invalid_input", error.GetProperty("code").GetString());
        Assert.Equal("--redirect-uri must be an absolute URI.", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("youtube", "YouTube Desktop OAuth redirect")]
    [InlineData("x", "X Native App redirect URI")]
    public async Task Connect_reaches_provider_redirect_validation(string provider, string expectedMessage)
    {
        var result = await InvokeConnectAsync(provider, "https://127.0.0.1:8765/callback");
        Assert.NotEqual(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("invalid_input", error.GetProperty("code").GetString());
        Assert.Contains(expectedMessage, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_enqueue_is_idempotent_and_worker_publishes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "release.json");
        await File.WriteAllTextAsync(manifest, """
{"schemaVersion":1,"clientRequestId":"cli-release","content":{"text":"hello"},"targets":[{"account":"main","provider":"fake"}]}
""");
        var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
        Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
        try
        {
            var first = await InvokeAsync(["--data-dir", directory, "post", "--file", manifest]);
            Assert.Equal(0, first.ExitCode);
            using var firstJson = JsonDocument.Parse(first.Output);
            var postId = firstJson.RootElement.GetProperty("result").GetProperty("postId").GetGuid();
            Assert.False(firstJson.RootElement.GetProperty("result").GetProperty("existing").GetBoolean());

            var second = await InvokeAsync(["--data-dir", directory, "post", "--file", manifest]);
            Assert.Equal(0, second.ExitCode);
            using var secondJson = JsonDocument.Parse(second.Output);
            Assert.Equal(postId, secondJson.RootElement.GetProperty("result").GetProperty("postId").GetGuid());
            Assert.True(secondJson.RootElement.GetProperty("result").GetProperty("existing").GetBoolean());

            Assert.Equal(0, (await InvokeAsync(["--data-dir", directory, "worker", "once"])).ExitCode);
            var status = await InvokeAsync(["--data-dir", directory, "post", "status", postId.ToString("D")]);
            using var statusJson = JsonDocument.Parse(status.Output);
            Assert.Equal("Published", statusJson.RootElement.GetProperty("result").GetProperty("publications")[0].GetProperty("state").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", previousProfile);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Manifest_accepts_youtube_video_and_require_approval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-cli-youtube-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "youtube.json");
        var video = Path.Combine(directory, "clip.mp4");
        await File.WriteAllBytesAsync(video,
            [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]);
        await File.WriteAllTextAsync(manifest, """
{"schemaVersion":1,"clientRequestId":"youtube-video","content":{"title":"Video title","text":"description","video":"clip.mp4"},"targets":[{"account":"yt-main","provider":"youtube","visibility":"public","approvalPolicy":"RequireApproval","options":{"madeForKids":false,"containsSyntheticMedia":true,"uploadNoticeAcknowledged":true}}]}
""");

        var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
        var previousKey = Environment.GetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY");
        var key = new byte[32];
        Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
        Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", Convert.ToBase64String(key));
        try
        {
            await using (var setupRuntime = await RuntimeFactory.CreateAsync(directory, new InMemoryMasterKeyStore(key)))
            {
                var expires = DateTimeOffset.UtcNow.AddHours(2);
                var material = JsonSerializer.SerializeToUtf8Bytes(new TokenMaterial("access", "refresh", expires));
                var blob = await setupRuntime.Store.PutAsync("auth-token", material);
                _ = await setupRuntime.Store.SaveConnectedAccountAsync(new(
                    "youtube", "yt-main", "channel-1", "YT Main", "desktop-client",
                    "https://www.googleapis.com/auth/youtube.force-ssl", expires, blob));
            }

            var result = await InvokeAsync(["--data-dir", directory, "post", "--file", manifest]);
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            var publicationId = json.RootElement.GetProperty("result").GetProperty("publicationIds")[0].GetGuid();

            await using var runtime = await RuntimeFactory.CreateAsync(directory, new InMemoryMasterKeyStore(key));
            var approval = await runtime.Operations.PublicationApprovalAsync(publicationId);
            Assert.Equal(ApprovalPolicy.RequireApproval, approval.Policy);
            Assert.False(approval.Approved);
            Assert.True(approval.CanApprove);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", previousKey);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Manifest_rejects_youtube_without_made_for_kids_and_notice_acknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-cli-youtube-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "youtube-invalid.json");
        await File.WriteAllTextAsync(manifest, """
{"schemaVersion":1,"clientRequestId":"youtube-invalid","content":{"title":"Video title","video":"clip.mp4"},"targets":[{"account":"yt-main","provider":"youtube","visibility":"private","options":{}}]}
""");
        var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
        var previousKey = Environment.GetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY");
        var key = new byte[32];
        Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
        Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", Convert.ToBase64String(key));
        try
        {
            var result = await InvokeAsync(["--data-dir", directory, "post", "--file", manifest]);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("madeForKids", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY", previousKey);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(string[] args)
    {
        var original = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        Console.SetOut(output);
        try
        {
            var exit = await CliApplication.RunAsync(args);
            return (exit, output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    private static async Task<(int ExitCode, string Output)> InvokeConnectAsync(string provider, string redirect)
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-cli-oauth-tests", Guid.NewGuid().ToString("N"));
        var previousProfile = Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE");
        Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", "test");
        try
        {
            return await InvokeAsync(["--data-dir", directory, "account", "connect", provider,
                "--client-id", "test-client", "--redirect-uri", redirect]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POST_ROUTER_PROFILE", previousProfile);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
