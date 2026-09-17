namespace PostRouter.Application;

public sealed record GitHubMediaConfiguration(string Owner, string Repository, string ReleaseTag, string? CredentialBlobId);
public sealed record GitHubMediaConfigurationStatus(string Owner, string Repository, string ReleaseTag, bool CredentialConfigured);

public interface IGitHubMediaConfigurationStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
    Task<GitHubMediaConfiguration?> ReadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GitHubMediaConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class GitHubMediaConfigurationService(IGitHubMediaConfigurationStore settings, IVault vault)
{
    public const string CredentialPurpose = "github-media-staging-token";

    public async Task<GitHubMediaConfigurationStatus?> StatusAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        return current is null ? null : Status(current);
    }

    public async Task<GitHubMediaConfigurationStatus> ConfigureAsync(string owner, string repository, string releaseTag,
        CancellationToken cancellationToken = default)
    {
        if (!ValidSegment(owner) || !ValidSegment(repository) || !ValidSegment(releaseTag))
            throw new ArgumentException("GitHub media staging settings are invalid.");
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sameRepository = previous is not null
            && string.Equals(previous.Owner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Repository, repository, StringComparison.OrdinalIgnoreCase);
        var updated = new GitHubMediaConfiguration(owner, repository, releaseTag,
            sameRepository ? previous!.CredentialBlobId : null);
        await settings.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (!sameRepository && previous?.CredentialBlobId is { } oldBlob)
            await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(updated);
    }

    public async Task<GitHubMediaConfigurationStatus> SetCredentialAsync(ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default)
    {
        if (token.IsEmpty || token.Length > 1024 || token.Span.IndexOfAnyExceptInRange((byte)33, (byte)126) >= 0)
            throw new ArgumentException("GitHub credential is invalid.");
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("github_media_settings_missing");
        var blobId = await vault.PutAsync(CredentialPurpose, token, cancellationToken).ConfigureAwait(false);
        try { await settings.SaveAsync(previous with { CredentialBlobId = blobId }, cancellationToken).ConfigureAwait(false); }
        catch
        {
            await vault.DeleteAsync(blobId, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (previous.CredentialBlobId is { } oldBlob)
            await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(previous with { CredentialBlobId = blobId });
    }

    public async Task<GitHubMediaConfigurationStatus> ClearCredentialAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await settings.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await settings.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("github_media_settings_missing");
        var updated = previous with { CredentialBlobId = null };
        await settings.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (previous.CredentialBlobId is { } oldBlob)
            await vault.DeleteAsync(oldBlob, cancellationToken).ConfigureAwait(false);
        return Status(updated);
    }

    private static GitHubMediaConfigurationStatus Status(GitHubMediaConfiguration value) =>
        new(value.Owner, value.Repository, value.ReleaseTag, value.CredentialBlobId is not null);

    private static bool ValidSegment(string? value) =>
        value is { Length: > 0 and <= 100 } && value[0] != '.' && value[^1] != '.'
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
