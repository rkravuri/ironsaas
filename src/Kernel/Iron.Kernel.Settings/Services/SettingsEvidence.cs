using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.History;
using Iron.Kernel.Settings.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// The security settings of an evidence pack (TF-067 AC1, ADR-0052): the password and lockout policy, the MFA policy, the
/// session limits, impersonation, the signing policy and the four-eyes control, each as the tenant had it at the
/// period's end, rebuilt from the settings' own history (TF-058), with who set it and when.
/// </summary>
public sealed class SettingsEvidence(SettingsDbContext context, SettingDeclarations declarations, IPrincipals? principals = null) : IEvidenceSource
{
    /// <summary>The settings an auditor reads as security configuration, by the start of their keys.</summary>
    public static readonly IReadOnlyList<string> SecurityPrefixes = ["identity.password.", "identity.lockout.", "identity.mfa.", "identity.session.", "identity.impersonation.", "compliance.signing.", "changes.four_eyes."];

    private static readonly JsonSerializerOptions Compact = new();

    public async Task<IReadOnlyList<EvidenceTable>> TablesAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var security = declarations.All.Where(s => SecurityPrefixes.Any(p => s.Definition.Key.StartsWith(p, StringComparison.Ordinal))).OrderBy(s => s.Definition.Key, StringComparer.Ordinal).ToList();
        var keys = security.Select(s => s.Definition.Key).ToArray();
        var changes = await context.History.AsNoTracking()
            .Where(c => keys.Contains(c.Key) && c.Level == SettingLevel.Tenant && c.CreatedAt < period.To)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var last = changes.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var people = principals is null || last.Count == 0
            ? new Dictionary<Guid, PrincipalSummary>()
            : await principals.FindAsync([.. last.Values.Select(c => c.CreatedBy).Distinct()], cancellationToken).ConfigureAwait(false);

        var rows = security.Select(s =>
        {
            var change = last.GetValueOrDefault(s.Definition.Key);
            var set = change?.After is not null;
            return (IReadOnlyList<string?>)
            [
                s.Definition.Key,
                s.Definition.Description,
                set ? Compactly(change!.After!) : JsonSerializer.Serialize(s.Definition.Default, Compact),
                set ? "the tenant" : "the default",
                s.Definition.GovernedArea,
                change is null ? null : CanonicalJson.Time(change.CreatedAt),
                change is null ? null : people.TryGetValue(change.CreatedBy, out var person) ? person.DisplayName : change.CreatedBy == SystemPrincipal.SystemId ? "System" : change.CreatedBy.ToString("D"),
            ];
        }).ToList();

        return
        [
            new EvidenceTable(EvidenceDocuments.SecuritySettings, "settings", "Security settings at the period's end",
                [new("key", "Setting"), new("meaning", "What it sets"), new("value", "Value"), new("from", "Set by"), new("four_eyes_area", "Four-eyes area"), new("changed_at", "Last changed (UTC)"), new("changed_by", "Last changed by")],
                rows),
        ];
    }

    private static string Compactly(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, Compact);
    }
}
