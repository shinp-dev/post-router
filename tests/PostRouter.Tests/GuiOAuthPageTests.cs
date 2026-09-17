using PostRouter.Application;
using PostRouter.Gui;

namespace PostRouter.Tests;

public sealed class GuiOAuthPageTests
{
    [Fact]
    public void Provider_failure_shows_safe_code_without_exception_details()
    {
        var failure = new ProviderOperationException("youtube_channel_lookup_rejected", false,
            new InvalidOperationException("authorization-code access-token refresh-token client-secret raw-response"));

        var html = GuiApplication.OAuthFailurePage(failure);

        Assert.Contains("Connection failed", html, StringComparison.Ordinal);
        Assert.Contains("youtube_channel_lookup_rejected", html, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-response", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("oauth_state_mismatch")]
    [InlineData("oauth_scope_missing")]
    [InlineData("oauth_refresh_token_missing")]
    [InlineData("reconnect_account_mismatch")]
    [InlineData("auth_required")]
    public void Known_internal_failure_shows_fixed_code(string safeCode)
    {
        var html = GuiApplication.OAuthFailurePage(new InvalidOperationException(safeCode));
        Assert.Contains($"<code>{safeCode}</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_failure_uses_generic_code_without_raw_message()
    {
        var html = GuiApplication.OAuthFailurePage(new IOException("authorization-code=secret raw provider response"));
        Assert.Contains("<code>connection_failed</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw provider response", html, StringComparison.Ordinal);

        var unknownState = GuiApplication.OAuthFailurePage(new InvalidOperationException("oauth_unrecognized_secret"));
        Assert.Contains("<code>connection_failed</code>", unknownState, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth_unrecognized_secret", unknownState, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_log_excludes_client_secret_and_exception_message()
    {
        var failure = new InvalidOperationException("client-secret-marker authorization-code-marker");
        var log = GuiApplication.OAuthFailureLog(failure);
        Assert.Contains("InvalidOperationException (connection_failed)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-marker", log, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-code-marker", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_provider_safe_code_uses_generic_code()
    {
        var html = GuiApplication.OAuthFailurePage(new ProviderOperationException("<script>secret</script>", false));
        Assert.Contains("<code>connection_failed</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_connection_keeps_connected_page()
    {
        var html = GuiApplication.OAuthSuccessPage();
        Assert.Contains("<h1>Connected</h1>", html, StringComparison.Ordinal);
        Assert.Contains("The account is connected. You may close this window.", html, StringComparison.Ordinal);
    }
}
