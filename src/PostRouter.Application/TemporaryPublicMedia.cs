using PostRouter.Domain;

namespace PostRouter.Application;

// Persist this operation before StageAsync. RecoverAsync is read-only and can resolve
// an upload whose response was lost without sending the media a second time.
public sealed record PublicMediaStagingOperation(string Value)
{
    public override string ToString() => "[OPAQUE MEDIA STAGING OPERATION]";
}

public sealed record StagedPublicAssetHandle(string Value)
{
    public override string ToString() => "[OPAQUE STAGED MEDIA HANDLE]";
}

public sealed record StagedPublicAsset(
    Uri PublicUrl, StagedPublicAssetHandle Handle, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string SourceSha256, long SourceSizeBytes);

public sealed class TemporaryPublicMediaException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface ITemporaryPublicMediaHost
{
    PublicMediaStagingOperation Prepare(MediaAsset asset, DateTimeOffset? expiresAt = null);
    Task<StagedPublicAsset> StageAsync(MediaAsset asset, PublicMediaStagingOperation operation, CancellationToken cancellationToken = default);
    Task<StagedPublicAsset?> RecoverAsync(PublicMediaStagingOperation operation, CancellationToken cancellationToken = default);
    Task DeleteAsync(StagedPublicAssetHandle handle, CancellationToken cancellationToken = default);
}
