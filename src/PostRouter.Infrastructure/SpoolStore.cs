using System.Security.Cryptography;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed class SpoolStore(string root, long maximumBytes = 8L * 1024 * 1024 * 1024) : ISpoolStore
{
    private readonly string _root = Path.GetFullPath(root);

    public async Task<MediaAsset> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Media file was not found.", source);
        var info = new FileInfo(source);
        if (info.Length <= 0 || info.Length > maximumBytes) throw new InvalidDataException("Media size is outside the configured limit.");
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string hash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied = checked(copied + read);
                    if (copied > maximumBytes) throw new InvalidDataException("Media grew beyond the configured size limit while being copied.");
                    sha.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            var mime = await DetectMimeAsync(temp, cancellationToken);
            var extension = mime switch { "image/jpeg" => ".jpg", "video/mp4" => ".mp4", _ => ".bin" };
            var destination = Path.Combine(_root, hash + extension);
            if (File.Exists(destination)) File.Delete(temp); else File.Move(temp, destination);
            return new(Guid.NewGuid(), hash, new FileInfo(destination).Length, mime, destination);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    public async Task<bool> VerifyAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(asset.StorageRef);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, comparison) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, asset.Sha256, StringComparison.Ordinal) && stream.Length == asset.SizeBytes;
    }

    private static async Task<string> DetectMimeAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = new byte[12];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(bytes, cancellationToken);
        if (read >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (read >= 12 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p') return "video/mp4";
        throw new InvalidDataException("Unsupported or unexpected media type.");
    }
}
