using System.Security.Cryptography;

namespace PostRouter.Application;

public sealed record InstagramOAuthConfiguration(string AppId, string? SecretBlobId);
public sealed record InstagramOAuthConfigurationStatus(string? AppId, bool AppSecretConfigured, string RedirectUri);

public interface IInstagramOAuthConfigurationStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
    Task<InstagramOAuthConfiguration?> ReadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(InstagramOAuthConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class InstagramOAuthConfigurationService(IInstagramOAuthConfigurationStore settings, IVault vault)
{
    private const string Purpose = "instagram-app-secret";
    public const string RedirectUri = "https://auth.shinp-studio.com/instagram/callback";

    public async Task<InstagramOAuthConfigurationStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        Status(await settings.ReadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<InstagramOAuthConfigurationStatus> SetAppIdAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 || !appId.All(char.IsAsciiDigit))
            throw new ArgumentException("Instagram App ID is invalid.");
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        var updated = new InstagramOAuthConfiguration(appId,
            string.Equals(previous?.AppId, appId, StringComparison.Ordinal) ? previous!.SecretBlobId : null);
        await settings.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (updated.SecretBlobId is null && previous?.SecretBlobId is { } oldBlob)
            await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(updated);
    }

    public async Task<InstagramOAuthConfigurationStatus> SetAppSecretAsync(ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        if (secret.IsEmpty || secret.Length > 1024 || secret.Span.IndexOfAnyExceptInRange((byte)33, (byte)126) >= 0)
            throw new ArgumentException("Instagram App Secret is invalid.");
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("instagram_app_id_missing");
        var blob = await vault.PutAsync(Purpose, secret, cancellationToken).ConfigureAwait(false);
        try { await settings.SaveAsync(previous with { SecretBlobId = blob }, cancellationToken).ConfigureAwait(false); }
        catch
        {
            await vault.DeleteAsync(blob, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (previous.SecretBlobId is { } oldBlob) await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(previous with { SecretBlobId = blob });
    }

    public async Task<string> ReadAppSecretAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration?.SecretBlobId is null) throw new InvalidOperationException("instagram_app_secret_missing");
        var bytes = await vault.GetAsync(configuration.SecretBlobId, Purpose, cancellationToken).ConfigureAwait(false);
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<string> ReadAppIdAsync(CancellationToken cancellationToken = default) =>
        (await settings.ReadAsync(cancellationToken).ConfigureAwait(false))?.AppId
        ?? throw new InvalidOperationException("instagram_app_id_missing");

    public async Task<InstagramOAuthConfigurationStatus> ClearAppSecretAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("instagram_app_id_missing");
        await settings.SaveAsync(previous with { SecretBlobId = null }, cancellationToken).ConfigureAwait(false);
        if (previous.SecretBlobId is { } oldBlob) await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(previous with { SecretBlobId = null });
    }

    private static InstagramOAuthConfigurationStatus Status(InstagramOAuthConfiguration? configuration) =>
        new(configuration?.AppId, configuration?.SecretBlobId is not null, RedirectUri);
}
