using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.History;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Identity's part of an evidence pack (TF-067 AC1, ADR-0052): every person and service account as they stood at the
/// period's end, the company logins then without their secrets, the emergency accounts, and the tenant's documents in
/// force. The state at the period's end is today's with every save the field history recorded since undone (ADR-0051).
/// </summary>
public sealed class IdentityEvidence(IdentityDbContext context, IRecordHistory history, IOrganisationUnits units) : IEvidenceSource
{
    public async Task<IReadOnlyList<EvidenceTable>> TablesAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var (principalType, principalNow) = TrackedFields.Of(context, await context.Principals.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var principals = await history.AsOfAsync(principalType, principalNow, period.To, cancellationToken).ConfigureAwait(false);
        var (providerType, providerNow) = TrackedFields.Of(context, await context.SsoProviders.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var providers = await history.AsOfAsync(providerType, providerNow, period.To, cancellationToken).ConfigureAwait(false);
        var (documentType, documentNow) = TrackedFields.Of(context, await context.TenantDocuments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var documents = await history.AsOfAsync(documentType, documentNow, period.To, cancellationToken).ConfigureAwait(false);

        var unitNames = new Dictionary<Guid, string>();
        foreach (var unit in principals.Values.Select(p => Guid.TryParse(p.GetValueOrDefault("home_unit_id"), out var id) ? id : (Guid?)null).OfType<Guid>().Distinct())
        {
            if (await units.FindAsync(unit, cancellationToken).ConfigureAwait(false) is { } found)
            {
                unitNames[unit] = found.Name;
            }
        }

        var ordered = principals.OrderBy(p => p.Value.GetValueOrDefault("login_id"), StringComparer.Ordinal).ToList();
        var people = ordered.Select(p => (IReadOnlyList<string?>)
        [
            p.Key.ToString("D"),
            p.Value.GetValueOrDefault("login_id"),
            p.Value.GetValueOrDefault("display_name"),
            Words(p.Value.GetValueOrDefault("type")),
            Words(p.Value.GetValueOrDefault("status")),
            Guid.TryParse(p.Value.GetValueOrDefault("home_unit_id"), out var home) ? unitNames.GetValueOrDefault(home, home.ToString("D")) : null,
            YesNo(p.Value.GetValueOrDefault("sso_only")),
            YesNo(p.Value.GetValueOrDefault("emergency")),
            p.Value.GetValueOrDefault("break_glass_until") is { } until ? $"until {until}" : null,
        ]).ToList();

        var emergency = ordered.Where(p => p.Value.GetValueOrDefault("emergency") == "true")
            .Select(p => (IReadOnlyList<string?>)[p.Value.GetValueOrDefault("login_id"), p.Value.GetValueOrDefault("display_name"), Words(p.Value.GetValueOrDefault("status"))])
            .ToList();

        var logins = providers.Values.OrderBy(p => p.GetValueOrDefault("name"), StringComparer.Ordinal).Select(p => (IReadOnlyList<string?>)
        [
            p.GetValueOrDefault("name"),
            p.GetValueOrDefault("preset"),
            p.GetValueOrDefault("discovery_url"),
            p.GetValueOrDefault("client_id"),
            List(p.GetValueOrDefault("allowed_domains")),
            YesNo(p.GetValueOrDefault("sso_only")),
            YesNo(p.GetValueOrDefault("trust_provider_mfa")),
            YesNo(p.GetValueOrDefault("just_in_time")),
            p.GetValueOrDefault("default_role_code"),
            p.GetValueOrDefault("role_mappings"),
        ]).ToList();

        var inForce = documents.Values
            .Where(d => d.GetValueOrDefault("retired") != "true")
            .GroupBy(d => d.GetValueOrDefault("key"), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(d => int.TryParse(d.GetValueOrDefault("number"), out var number) ? number : 0).First())
            .OrderBy(d => d.GetValueOrDefault("key"), StringComparer.Ordinal)
            .Select(d => (IReadOnlyList<string?>)[d.GetValueOrDefault("key"), d.GetValueOrDefault("number"), d.GetValueOrDefault("title"), d.GetValueOrDefault("published_at")])
            .ToList();

        const string note = "The state at the period's end is today's with every change the field history recorded since undone; the field history began with S3 T7 (ADR-0051).";
        return
        [
            new EvidenceTable(EvidenceDocuments.AccessList, "people_and_accounts", "People and service accounts at the period's end",
                [new("id", "Id"), new("login_id", "Login identifier"), new("name", "Name"), new("type", "Type"), new("status", "Status"), new("home_unit", "Home unit"), new("sso_only", "Company login only"), new("emergency", "Emergency account"), new("break_glass", "Break-glass account")],
                people, note),
            new EvidenceTable(EvidenceDocuments.SecuritySettings, "company_logins", "Company logins at the period's end, without their secrets",
                [new("name", "Name"), new("preset", "Kind"), new("discovery_url", "Discovery address"), new("client_id", "Client id"), new("allowed_domains", "Allowed domains"), new("sso_only", "Company login only"), new("trust_provider_mfa", "Trusts the provider's second factor"), new("just_in_time", "Creates people at first sign-in"), new("default_role", "Default role"), new("role_mappings", "Group mappings")],
                logins, logins.Count == 0 ? "No company login stood at the period's end." : null),
            new EvidenceTable(EvidenceDocuments.SecuritySettings, "emergency_accounts", "Emergency accounts at the period's end",
                [new("login_id", "Login identifier"), new("name", "Name"), new("status", "Status")],
                emergency, emergency.Count == 0 ? "No emergency account stood at the period's end." : null),
            new EvidenceTable(EvidenceDocuments.SecuritySettings, "tenant_documents", "Tenant documents in force at the period's end",
                [new("key", "Document"), new("version", "Version"), new("title", "Title"), new("published_at", "Published (UTC)")],
                inForce, inForce.Count == 0 ? "The tenant had published no document of its own; the platform's terms are the operator's." : null),
        ];
    }

    private static string? Words(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => "person",
        "serviceaccount" => "service account",
        "apikey" => "API key",
        null => null,
        var other => other,
    };

    private static string? YesNo(string? value) => value switch
    {
        "true" => "yes",
        "false" => "no",
        _ => value,
    };

    private static string? List(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            return string.Join(", ", JsonSerializer.Deserialize<List<string>>(json) ?? []);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
