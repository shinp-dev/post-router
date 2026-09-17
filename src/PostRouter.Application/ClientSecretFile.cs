using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PostRouter.Application;

public static class ClientSecretFile
{
    public const int MaximumBytes = 4096;

    public static async Task<string> ReadAsync(Stream stream, long length, CancellationToken cancellationToken = default)
    {
        if (length is < 1 or > MaximumBytes)
            throw new ArgumentException("Client secret file must contain 1 to 4096 bytes.");
        var bytes = new byte[MaximumBytes + 1];
        try
        {
            var read = 0;
            int count;
            while (read < bytes.Length &&
                   (count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false)) != 0)
                read += count;
            if (read > MaximumBytes) throw new ArgumentException("Client secret file must contain 1 to 4096 bytes.");
            string value;
            try { value = new UTF8Encoding(false, true).GetString(bytes, 0, read).TrimStart('\uFEFF').TrimEnd('\r', '\n'); }
            catch (DecoderFallbackException) { throw new ArgumentException("Client secret file must be UTF-8 text."); }
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Client secret file is empty.");
            if (value.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(value);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("installed", out var installed) ||
                        installed.ValueKind != JsonValueKind.Object ||
                        !installed.TryGetProperty("client_secret", out var secret) ||
                        secret.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(secret.GetString()))
                        throw new ArgumentException("Client secret file format is invalid.");
                    return secret.GetString()!;
                }
                catch (JsonException) { throw new ArgumentException("Client secret file format is invalid."); }
            }
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
