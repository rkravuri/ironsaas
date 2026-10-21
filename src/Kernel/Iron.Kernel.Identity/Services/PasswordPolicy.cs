using System.Collections.Frozen;
using System.Reflection;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The list of common passwords the policy refuses (TF-012 AC1): the NCSC's 100,000 most used, bundled with the module
/// (<c>common-passwords.txt</c>, its source and licence in <c>docs/licences.md</c>). Compared without case, so
/// "Password1234" is as common as "password1234". Only entries of eight characters or more are kept in memory, because a
/// shorter password fails the length rule first.
/// </summary>
public sealed class CommonPasswords
{
    public const string ResourceName = "Iron.Kernel.Identity.Services.common-passwords.txt";

    /// <summary>The shortest minimum length a tenant may set; a shorter entry can never be the deciding rule.</summary>
    private const int ShortestAllowed = 8;

    private readonly Lazy<FrozenSet<string>> _entries = new(Load);

    public int Count => _entries.Value.Count;

    public bool Contains(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _entries.Value.Contains(password);
    }

    private static FrozenSet<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource {ResourceName} is missing from the Identity assembly.");
        using var reader = new StreamReader(stream);
        var entries = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length >= ShortestAllowed)
            {
                entries.Add(line);
            }
        }

        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The tenant's password rules as the shell shows them in words (TF-012), and the API applies them.</summary>
public sealed record PasswordRules(int MinimumLength, int MaximumLength, bool CommonCheck, int HistoryDepth, int MaximumAgeDays)
{
    /// <summary>The days before the maximum age when the person is warned.</summary>
    public const int WarningDays = 14;
}

/// <summary>
/// The password policy (TF-012, ADR-0046): one check for every path that sets a password (an invitation, a recovery link,
/// a change), with the tenant's settings: a minimum length, at most 1,024 characters, not the login identifier, not on the
/// common list, and not one of the person's last N passwords. No composition rules. A refusal names the rule it failed
/// as the field error's code, so the shell says which one.
/// </summary>
public sealed class PasswordPolicy(IdentityDbContext context, ISettings settings, CommonPasswords common, IPasswordHasher hasher, IdentityOptions options)
{
    /// <summary>The field error codes: one per rule.</summary>
    public static class Rules
    {
        public const string TooShort = "too_short";
        public const string TooLong = "too_long";
        public const string IsLoginId = "is_login_id";
        public const string Common = "common";
        public const string Reused = "reused";
    }

    public async Task<PasswordRules> RulesAsync(CancellationToken cancellationToken)
    {
        var minimum = await settings.GetAsync(IdentitySettings.PasswordMinimumLength, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var commonCheck = await settings.GetAsync(IdentitySettings.PasswordCommonCheck, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var history = await settings.GetAsync(IdentitySettings.PasswordHistoryDepth, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var age = await settings.GetAsync(IdentitySettings.PasswordMaximumAgeDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        return new PasswordRules(minimum, options.Password.MaximumLength, commonCheck, history, age);
    }

    /// <summary>
    /// Checks a new password for <paramref name="principal"/>. The history is read here, newest first; each entry costs
    /// one Argon2id verification, so the check stops at the first match.
    /// </summary>
    public async Task<Result> CheckAsync(Principal principal, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(password);
        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        var simple = Check(rules, principal.LoginId, password);
        if (simple.IsFailure || rules.HistoryDepth == 0)
        {
            return simple;
        }

        var recent = await context.PasswordHistory.AsNoTracking()
            .Where(e => e.PrincipalId == principal.Id)
            .OrderByDescending(e => e.SetAt)
            .Take(rules.HistoryDepth)
            .Select(e => e.PasswordHash)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A person from before the history began has their current password only on their row.
        if (recent.Count == 0 && principal.PasswordHash is { } current)
        {
            recent.Add(current);
        }

        if (recent.Any(hash => hasher.Verify(password, hash)))
        {
            return Refuse(IdentityErrors.PasswordReused, Rules.Reused, $"Choose a password you have not used before: your last {rules.HistoryDepth} cannot be used again.", "Choose a password you have not used recently.");
        }

        return Result.Success();
    }

    /// <summary>The rules that need no database: length, the login identifier, the common list.</summary>
    public Result Check(PasswordRules rules, string loginId, string password)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < rules.MinimumLength)
        {
            return Refuse(IdentityErrors.PasswordTooShort, Rules.TooShort, $"The password needs at least {rules.MinimumLength} characters.", $"At least {rules.MinimumLength} characters.");
        }

        if (password.Length > rules.MaximumLength)
        {
            return Refuse(IdentityErrors.PasswordTooLong, Rules.TooLong, $"The password may have at most {rules.MaximumLength} characters.", $"At most {rules.MaximumLength} characters.");
        }

        if (string.Equals(password.Trim(), loginId, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(IdentityErrors.PasswordIsLoginId, Rules.IsLoginId, "The password must differ from the login identifier.", "Choose a password that is not your login.");
        }

        if (rules.CommonCheck && common.Contains(password))
        {
            return Refuse(IdentityErrors.PasswordCommon, Rules.Common, "This password is on the list of the most common passwords, which are guessed first. Choose another.", "Choose a password that is not common.");
        }

        return Result.Success();
    }

    private static Error Refuse(string code, string rule, string message, string fieldMessage) =>
        Error.Validation(code, message, new FieldError("password", rule, fieldMessage));
}
