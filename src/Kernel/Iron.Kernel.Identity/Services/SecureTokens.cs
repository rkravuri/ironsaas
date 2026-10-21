using System.Security.Cryptography;
using System.Text;

namespace Iron.Kernel.Identity.Services;

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

/// <summary>
/// The links a person opens (ADR-0046): the shell's page, with the code in the fragment. A browser never sends a fragment
/// to a server, so the code is in no access log, referrer or trace on the way; the page posts it in a request's body.
/// </summary>
public static class Links
{
    public static string Invitation(string token) => $"/invitations#{token}";

    public static string Recovery(string code) => $"/recovery#{code}";
}
