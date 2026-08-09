using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Security;

/// <summary>
/// AES-GCM encryption for Binolla SSID at rest. Key from BINOLLA_TOKEN_ENCRYPTION_KEY (32+ chars / base64).
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(IConfiguration configuration)
    {
        var configured = configuration["BINOLLA_TOKEN_ENCRYPTION_KEY"]
                         ?? configuration["Security:BinollaTokenEncryptionKey"]
                         ?? throw new InvalidOperationException("BINOLLA_TOKEN_ENCRYPTION_KEY is required.");

        _key = DeriveKey(configured);
    }

    public string Encrypt(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        var payload = Convert.FromBase64String(ciphertext);
        if (payload.Length < 12 + 16)
            throw new CryptographicException("Invalid ciphertext.");

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string configured)
    {
        if (TryFromBase64(configured, out var key) && key.Length == 32)
            return key;

        return SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    }

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
