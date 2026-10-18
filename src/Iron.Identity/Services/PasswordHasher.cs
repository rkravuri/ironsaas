using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Iron.Identity.Services;

/// <summary>Hashes and verifies passwords. The stored form never reveals the password and carries its own parameters.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string encodedHash);
}

/// <summary>
/// Argon2id with a 16-byte random salt per password and a 32-byte hash, stored as a PHC string
/// (<c>$argon2id$v=19$m=65536,t=3,p=1$salt$hash</c>) so the parameters can change without breaking old hashes (TF-011 AC3).
/// </summary>
public sealed class Argon2PasswordHasher(IdentityOptions options) : IPasswordHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var argon2 = options.Argon2;
        var hash = Compute(password, salt, argon2.MemoryKb, argon2.Iterations, argon2.Parallelism);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={argon2.MemoryKb},t={argon2.Iterations},p={argon2.Parallelism}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}");
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0 || !TryParse(encodedHash, out var parameters))
        {
            return false; // the library refuses an empty password; an empty password is simply wrong
        }

        var computed = Compute(password, parameters.Salt, parameters.MemoryKb, parameters.Iterations, parameters.Parallelism);
        return CryptographicOperations.FixedTimeEquals(computed, parameters.Hash);
    }

    public static bool IsArgon2id(string? encodedHash) => encodedHash is not null && encodedHash.StartsWith("$argon2id$", StringComparison.Ordinal);

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashLength);
    }

    private static bool TryParse(string? encoded, out (byte[] Salt, byte[] Hash, int MemoryKb, int Iterations, int Parallelism) parameters)
    {
        parameters = default;
        if (encoded is null)
        {
            return false;
        }

        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
        {
            return false;
        }

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var part in parts[2].Split(','))
        {
            var pair = part.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m":
                    memory = value;
                    break;
                case "t":
                    iterations = value;
                    break;
                case "p":
                    parallelism = value;
                    break;
                default:
                    return false;
            }
        }

        if (memory <= 0 || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        try
        {
            parameters = (FromBase64(parts[3]), FromBase64(parts[4]), memory, iterations, parallelism);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] FromBase64(string unpadded) => Convert.FromBase64String(unpadded.PadRight(unpadded.Length + ((4 - (unpadded.Length % 4)) % 4), '='));
}
