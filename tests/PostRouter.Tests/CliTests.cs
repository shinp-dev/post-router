using System.Text.Json;
using PostRouter.Cli;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PostRouter.Tests;

public sealed class CliTests
{
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
}
