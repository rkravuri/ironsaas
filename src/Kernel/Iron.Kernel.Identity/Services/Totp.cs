using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Time-based one-time codes (RFC 6238 over RFC 4226), as authenticator apps make them: HMAC-SHA1, 30-second steps, six
/// digits (ADR-0047). A code is accepted for the step before, the step of, and the step after the server's clock, and
/// only for a step later than the last one accepted, so a code that was used, or an older one, never works again.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public static readonly TimeSpan StepLength = TimeSpan.FromSeconds(30);

    /// <summary>How many steps either side of the server's clock are accepted: one, for a phone a few seconds off.</summary>
    public const int Tolerance = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new secret: 160 random bits, the length RFC 4226 recommends.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)StepLength.TotalSeconds;

    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        // RFC 6238's algorithm, which every authenticator app computes; an HMAC keyed with 160 random bits does not rest
        // on SHA-1's resistance to collisions, which is what CA5350 warns of (ADR-0047).
#pragma warning disable CA5350
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step whose code <paramref name="code"/> is, when it is within the tolerance of <paramref name="now"/> and later
    /// than <paramref name="lastUsedStep"/>; null otherwise. Every candidate is compared in fixed time.
    /// </summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var typed = Normalise(code);
        if (typed.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        for (var step = current - Tolerance; step <= current + Tolerance; step++)
        {
            var fits = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(typed));
            if (fits && (lastUsedStep is not { } last || step > last))
            {
                matched ??= step;
            }
        }

        return matched;
    }

    /// <summary>A typed code without the spaces and dashes people put in it.</summary>
    public static string Normalise(string? code) =>
        new([.. (code ?? string.Empty).Where(c => c is not (' ' or '-'))]);

    /// <summary>The <c>otpauth://</c> URI an authenticator app reads from the QR code (the Key Uri Format).</summary>
    public static string Uri(string issuer, string account, byte[] secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var label = $"{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}";
        return $"otpauth://totp/{label}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={(int)StepLength.TotalSeconds}";
    }

    /// <summary>RFC 4648 base32 without padding, as authenticator apps take a secret typed by hand.</summary>
    public static string Base32(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = new StringBuilder((data.Length * 8 / 5) + 1);
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            // Never more than 12 bits are waiting, so the buffer keeps 16 and never overflows.
            buffer = ((buffer << 8) | value) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            text.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var character in text.ToUpperInvariant().Where(c => c is not ('=' or ' ' or '-')))
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
