using System.Security.Cryptography;
using System.Text;

namespace Alpha.Api.Security;

public sealed class AesDataProtectionService(IConfiguration configuration) : IDataProtectionService
{
    private const string Prefix = "alpha:v1:";
    private readonly byte[] _key = LoadKey(configuration);

    public string Protect(string plaintext, string purpose)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Prefix + Convert.ToBase64String(nonce) + ":" + Convert.ToBase64String(tag) + ":" + Convert.ToBase64String(cipher);
    }

    public string LookupHash(string value, string purpose)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(_key);
        var input = System.Text.Encoding.UTF8.GetBytes(purpose + "\0" + (value ?? string.Empty).Trim());
        return Convert.ToHexString(hmac.ComputeHash(input)).ToLowerInvariant();
    }

    public string Unprotect(string value, string purpose)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 3) throw new CryptographicException("Invalid protected value.");
        var nonce = Convert.FromBase64String(parts[0]); var tag = Convert.FromBase64String(parts[1]); var cipher = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadKey(IConfiguration configuration)
    {
        var raw = configuration["Security:DataProtectionKey"];
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Security:DataProtectionKey is required. Supply a 32-byte Base64 key via environment/secret management.");
        var key = Convert.FromBase64String(raw);
        if (key.Length != 32) throw new InvalidOperationException("Security:DataProtectionKey must decode to exactly 32 bytes.");
        return key;
    }
}
