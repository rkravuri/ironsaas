using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>The providers the console fills a discovery address for (ADR-0048); any standard provider works as <see cref="Other"/>.</summary>
public static class SsoPresets
{
    public const string MicrosoftEntraId = "entra_id";
    public const string GoogleWorkspace = "google_workspace";
    public const string Okta = "okta";
    public const string Other = "other";

    public static IReadOnlyList<string> All => [MicrosoftEntraId, GoogleWorkspace, Okta, Other];
}

/// <summary>
/// A group the provider names in its groups claim, and the role a person in it holds at a scope while they are in it: the
/// whole tenant (no unit), or a unit with or without the units below it (ADR-0048).
/// </summary>
public sealed record SsoRoleMapping(string Group, string RoleCode, Guid? UnitId, bool IncludeChildren);

/// <summary>Everything an administrator sets on a provider but its client secret, which lives only in the store (ADR-0048).</summary>
public sealed record SsoProviderValues(
    string Name,
    string Preset,
    string DiscoveryUrl,
    string ClientId,
    IReadOnlyList<string> AllowedDomains,
    string EmailClaim,
    string NameClaim,
    string GroupsClaim,
    bool JustInTime,
    string? DefaultRoleCode,
    Guid? DefaultHomeUnitId,
    bool SsoOnly,
    bool TrustProviderMfa,
    IReadOnlyList<SsoRoleMapping> RoleMappings);

/// <summary>
/// An OpenID Connect identity provider of this tenant (TF-014, ADR-0048): its discovery address, the client this tenant is
/// at the provider, the email domains it may sign people in for, how its claims are read, and what happens to the people
/// it signs in: created on their first sign-in or not, with which role and home unit; whether everyone linked to it signs
/// in only through it; whether its assertion of multi-factor authentication stands for this product's second factor; and
/// which of its groups map to roles. The client secret is never here: it lives in the store at
/// <c>tenants/&lt;slug&gt;/sso/&lt;provider id&gt;</c>.
/// </summary>
public sealed class SsoProvider : AuditedEntity, ITenantWide, ITrackedChanges
{
    public const int NameMaxLength = 100;
    public const int PresetMaxLength = 20;
    public const int DiscoveryUrlMaxLength = 500;
    public const int ClientIdMaxLength = 200;
    public const int ClaimMaxLength = 100;
    public const int DomainMaxLength = 253;
    public const int GroupMaxLength = 200;
    public const int MaxDomains = 50;
    public const int MaxMappings = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private SsoProvider()
    {
        Name = string.Empty;
        Preset = SsoPresets.Other;
        DiscoveryUrl = string.Empty;
        ClientId = string.Empty;
        AllowedDomains = [];
        EmailClaim = "email";
        NameClaim = "name";
        GroupsClaim = "groups";
        RoleMappingsJson = "[]";
    }

    public SsoProvider(SsoProviderValues values)
        : this()
    {
        Apply(values);
    }

    /// <summary>The name the sign-in page's button shows: "Continue with &lt;name&gt;".</summary>
    public string Name { get; private set; }

    public string Preset { get; private set; }

    /// <summary>The address of the provider's discovery document (<c>.../.well-known/openid-configuration</c>).</summary>
    public string DiscoveryUrl { get; private set; }

    /// <summary>The client identifier this tenant has at the provider.</summary>
    public string ClientId { get; private set; }

    /// <summary>The email domains, in lowercase, whose people the provider may sign in, link and create (TF-014 AC3).</summary>
    public List<string> AllowedDomains { get; private set; }

    public string EmailClaim { get; private set; }

    public string NameClaim { get; private set; }

    public string GroupsClaim { get; private set; }

    /// <summary>A person of an allowed domain with no account here is created at their first sign-in (TF-014 AC1).</summary>
    public bool JustInTime { get; private set; }

    /// <summary>The role a person this provider creates is given, at their home unit and the units below it.</summary>
    public string? DefaultRoleCode { get; private set; }

    /// <summary>The home unit of a person this provider creates; the root when none is set.</summary>
    public Guid? DefaultHomeUnitId { get; private set; }

    /// <summary>Everyone linked to the provider signs in only through it (TF-014 AC2).</summary>
    public bool SsoOnly { get; private set; }

    /// <summary>
    /// When the ID token's <c>amr</c> says multi-factor authentication was used, it stands for this product's second factor.
    /// Off: this product's own second factor is asked too, by the tenant's MFA policy, whatever the provider says.
    /// </summary>
    public bool TrustProviderMfa { get; private set; }

    /// <summary>The group mapping as JSON (jsonb); read it through <see cref="RoleMappings"/>.</summary>
    public string RoleMappingsJson { get; private set; }

    public IReadOnlyList<SsoRoleMapping> RoleMappings => JsonSerializer.Deserialize<List<SsoRoleMapping>>(RoleMappingsJson, Json) ?? [];

    /// <summary>The source an assignment from this provider's mapping carries (ADR-0048).</summary>
    public string AssignmentSource => SourceOf(Id);

    public static string SourceOf(Guid providerId) => $"sso:{providerId:N}";

    public void Apply(SsoProviderValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Name = values.Name.Trim();
        Preset = values.Preset;
        DiscoveryUrl = values.DiscoveryUrl.Trim();
        ClientId = values.ClientId.Trim();
        AllowedDomains = [.. values.AllowedDomains.Select(NormaliseDomain).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        EmailClaim = values.EmailClaim.Trim();
        NameClaim = values.NameClaim.Trim();
        GroupsClaim = values.GroupsClaim.Trim();
        JustInTime = values.JustInTime;
        DefaultRoleCode = string.IsNullOrWhiteSpace(values.DefaultRoleCode) ? null : values.DefaultRoleCode.Trim();
        DefaultHomeUnitId = values.DefaultHomeUnitId;
        SsoOnly = values.SsoOnly;
        TrustProviderMfa = values.TrustProviderMfa;
        RoleMappingsJson = JsonSerializer.Serialize(values.RoleMappings.Select(m => m with { Group = m.Group.Trim(), RoleCode = m.RoleCode.Trim() }).ToList(), Json);
    }

    public SsoProviderValues Values() => new(Name, Preset, DiscoveryUrl, ClientId, AllowedDomains, EmailClaim, NameClaim, GroupsClaim, JustInTime, DefaultRoleCode, DefaultHomeUnitId, SsoOnly, TrustProviderMfa, RoleMappings);

    /// <summary>Whether an email belongs to one of the allowed domains, compared without case.</summary>
    public bool AllowsEmail(string? email) => DomainOf(email) is { } domain && AllowedDomains.Contains(domain, StringComparer.Ordinal);

    public static string NormaliseDomain(string? domain) => (domain ?? string.Empty).Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();

    /// <summary>The domain of an email, in lowercase; null for anything that is not an address.</summary>
    public static string? DomainOf(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (email is null || at <= 0 || at == email.Length - 1)
        {
            return null;
        }

        return NormaliseDomain(email[(at + 1)..]);
    }
}
