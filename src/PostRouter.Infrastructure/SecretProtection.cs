using System.Security.Cryptography;
using System.Text;

namespace PostRouter.Infrastructure;

public interface ISecretProtector
{
    ProtectedValue Protect(string purpose, ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(string purpose, ProtectedValue value);
}

public sealed record ProtectedValue(int KeyVersion, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    private readonly byte[] _key;
    public AesGcmSecretProtector(ReadOnlySpan<byte> key, int keyVersion = 1)
    {
        if (key.Length != 32) throw new ArgumentException("A 256-bit key is required.", nameof(key));
        _key = key.ToArray();
        KeyVersion = keyVersion;
    }
    public int KeyVersion { get; }

    public ProtectedValue Protect(string purpose, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
        return new(KeyVersion, nonce, tag, ciphertext);
    }

    public byte[] Unprotect(string purpose, ProtectedValue value)
    {
        if (value.KeyVersion != KeyVersion) throw new CryptographicException("Unknown key version.");
        var plaintext = new byte[value.Ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(value.Nonce, value.Ciphertext, value.Tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
