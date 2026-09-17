using System.Text;
using PostRouter.Application;

namespace PostRouter.Tests;

public sealed class ClientSecretFileTests
{
    [Fact]
    public async Task Plain_text_and_installed_app_json_use_the_same_safe_reader()
    {
        const string secret = "desktop-secret-marker";
        var plain = Encoding.UTF8.GetBytes(secret + "\n");
        await using var plainStream = new MemoryStream(plain);
        Assert.Equal(secret, await ClientSecretFile.ReadAsync(plainStream, plain.Length));

        var json = Encoding.UTF8.GetBytes("{\"installed\":{\"client_secret\":\"desktop-secret-marker\"}}");
        await using var jsonStream = new MemoryStream(json);
        Assert.Equal(secret, await ClientSecretFile.ReadAsync(jsonStream, json.Length));
    }

    [Fact]
    public async Task Invalid_json_does_not_echo_file_contents()
    {
        const string raw = "{client_secret: raw-secret-marker";
        var bytes = Encoding.UTF8.GetBytes(raw);
        await using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ClientSecretFile.ReadAsync(stream, bytes.Length));
        Assert.DoesNotContain("raw-secret-marker", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, error.ToString(), StringComparison.Ordinal);
    }
}
