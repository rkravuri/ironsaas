using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;

namespace Iron.Kernel.Identity.Services;

/// <summary>The tenant's MFA policy as one value (TF-013 AC1, ADR-0047).</summary>
public sealed record MfaRules(string RequiredFor, IReadOnlyList<string> Roles, string AllowedFactors, int GraceDays, bool PasskeySignIn)
{
    /// <summary>The factor kinds a person may enrol now.</summary>
    public IReadOnlyList<string> Allowed => AllowedFactors switch
    {
        MfaFactorChoice.AuthenticatorApp => [FactorKinds.AuthenticatorApp],
        MfaFactorChoice.Passkeys => [FactorKinds.Passkey],
        _ => FactorKinds.All,
    };

    public bool Allows(string kind) => Allowed.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// Whether the tenant's policy requires a second factor of a person (TF-013 AC1, ADR-0047): of nobody, of everyone, or of
/// the people who hold one of the listed roles at any scope; an empty list means every role that holds a high-risk or
/// critical permission, which is the default. It applies to people only: a service account keeps its client credentials.
/// </summary>
public sealed class MfaPolicy(ISettings settings, IRoleAssignments roleAssignments, PermissionCatalogue catalogue)
{
    public async Task<MfaRules> RulesAsync(CancellationToken cancellationToken)
    {
        var requiredFor = await settings.GetAsync(IdentitySettings.MfaRequiredFor, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var roles = await settings.GetAsync(IdentitySettings.MfaRoles, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var allowed = await settings.GetAsync(IdentitySettings.MfaAllowedFactors, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var grace = await settings.GetAsync(IdentitySettings.MfaGraceDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var passkeySignIn = await settings.GetAsync(IdentitySettings.MfaPasskeySignIn, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> codes = roles.ValueKind == JsonValueKind.Array
            ? [.. roles.EnumerateArray().Where(code => code.ValueKind == JsonValueKind.String).Select(code => code.GetString()!.Trim())]
            : [];
        return new MfaRules(requiredFor, codes, allowed, grace, passkeySignIn);
    }

    /// <summary>The grace a person has before enrolling: the tenant's, and none for an emergency account (ADR-0049).</summary>
    public static int GraceDaysFor(Principal principal, MfaRules rules)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(rules);
        return principal.Emergency ? 0 : rules.GraceDays;
    }

    public async Task<bool> IsRequiredAsync(Principal principal, MfaRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(rules);
        if (principal.Type != PrincipalType.User)
        {
            return false;
        }

        // An emergency account always has a second factor, whatever the policy says (ADR-0049).
        if (principal.Emergency)
        {
            return true;
        }

        switch (rules.RequiredFor)
        {
            case MfaRequirement.Everyone:
                return true;
            case MfaRequirement.Nobody:
                return false;
        }

        if (rules.Roles.Count > 0)
        {
            var held = await roleAssignments.RoleCodesAnywhereAsync(principal.Id, cancellationToken).ConfigureAwait(false);
            return held.Any(code => rules.Roles.Contains(code, StringComparer.Ordinal));
        }

        var permissions = await roleAssignments.PermissionsOfAsync(principal.Id, AccessScope.Anywhere, cancellationToken).ConfigureAwait(false);
        return permissions.Any(name => catalogue.Find(name)?.Risk is Risk.High or Risk.Critical);
    }
}
