using System.Security.Cryptography;
using System.Text;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Recovery codes (TF-013 AC3, ADR-0047): ten per person, made at the first enrolment and shown once. Each is 80 random
/// bits in sixteen base32 characters, grouped by four for reading; only the SHA-256 of the person's id and the code is
/// kept, which is enough for a value that cannot be guessed and lets a typed code be found by its hash. A code works once;
/// a new set replaces the old one whole.
/// </summary>
public sealed class RecoveryCodes(IdentityDbContext context, TimeProvider time)
{
    public const int Count = 10;
    private const int Characters = 16;

    /// <summary>Replaces the person's codes with ten new ones and answers them, the only time they are seen. Saves; the caller holds the transaction.</summary>
    public async Task<IReadOnlyList<string>> ReplaceAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await context.RecoveryCodes.Where(c => c.PrincipalId == principalId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var codes = Enumerable.Range(0, Count).Select(_ => NewCode()).ToList();
        context.RecoveryCodes.AddRange(codes.Select(code => new RecoveryCode(principalId, Hash(principalId, code))));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return codes;
    }

    /// <summary>Uses the code when it is one of the person's and unused: it never works again. Saves; the caller holds the transaction.</summary>
    public async Task<bool> UseAsync(Guid principalId, string? code, CancellationToken cancellationToken)
    {
        var normalised = Normalise(code);
        if (normalised.Length != Characters)
        {
            return false;
        }

        var hash = Hash(principalId, normalised);
        var found = await context.RecoveryCodes.SingleOrDefaultAsync(c => c.PrincipalId == principalId && c.CodeHash == hash && c.UsedAt == null, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return false;
        }

        found.Use(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<int> RemainingAsync(Guid principalId, CancellationToken cancellationToken) =>
        context.RecoveryCodes.CountAsync(c => c.PrincipalId == principalId && c.UsedAt == null, cancellationToken);

    /// <summary>A code as it is typed: case, spaces and dashes do not matter.</summary>
    public static string Normalise(string? code) =>
        new([.. (code ?? string.Empty).ToUpperInvariant().Where(c => c is not (' ' or '-'))]);

    public static string Hash(Guid principalId, string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{principalId:N}:{Normalise(code)}")));

    /// <summary>Sixteen base32 characters from ten random bytes, as XXXX-XXXX-XXXX-XXXX.</summary>
    private static string NewCode()
    {
        var text = Totp.Base32(RandomNumberGenerator.GetBytes(10));
        return string.Join('-', Enumerable.Range(0, Characters / 4).Select(group => text.Substring(group * 4, 4)));
    }
}
