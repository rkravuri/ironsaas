using System.Security.Cryptography;
using System.Text;

namespace Iron.Identity.Services;

/// <summary>Random tokens for sessions and invitations: 256 bits, base64url in the cookie or link, SHA-256 hex in the table.</summary>
public static class SecureTokens
{
    public const int HashLength = 64;

    public static string New() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
