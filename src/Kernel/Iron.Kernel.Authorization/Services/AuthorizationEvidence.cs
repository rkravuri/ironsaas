using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.History;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Authorization's part of an evidence pack (TF-067 AC1, ADR-0052): every role assignment at the period's end, with the
/// person, the role and where it applies; the roles with their permissions then; the grants and removals made in the
/// period, with who made them; and the conflict rules then. The state at the period's end is today's, with every save
/// the field history records since then undone (ADR-0051).
/// </summary>
public sealed class AuthorizationEvidence(AuthorizationDbContext context, IRecordHistory history, IPrincipals principals) : IEvidenceSource
{
    public async Task<IReadOnlyList<EvidenceTable>> TablesAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var (roleType, roleNow) = TrackedFields.Of(context, await context.Roles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var roles = await history.AsOfAsync(roleType, roleNow, period.To, cancellationToken).ConfigureAwait(false);
        var (assignmentType, assignmentNow) = TrackedFields.Of(context, await context.RoleAssignments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var assignments = await history.AsOfAsync(assignmentType, assignmentNow, period.To, cancellationToken).ConfigureAwait(false);
        var (ruleType, ruleNow) = TrackedFields.Of(context, await context.ConflictRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        var rules = await history.AsOfAsync(ruleType, ruleNow, period.To, cancellationToken).ConfigureAwait(false);
        var changes = await history.BetweenAsync(assignmentType, period.From, period.To, cancellationToken).ConfigureAwait(false);

        var units = await context.OrganisationUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
        var principalIds = assignments.Values.Select(a => Id(a, "principal_id"))
            .Concat(changes.Select(c => Id(c.Fields.ToDictionary(f => f.Field, f => f.New ?? f.Old), "principal_id")))
            .Concat(changes.Select(c => (Guid?)c.ActorId))
            .Concat(rules.Values.Select(r => Id(r, "owner_id")))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var people = await principals.FindAsync(principalIds, cancellationToken).ConfigureAwait(false);

        string Person(Guid? id) => id is { } known ? people.TryGetValue(known, out var person) ? person.DisplayName : known == SystemPrincipal.SystemId ? "System" : known.ToString("D") : string.Empty;
        string RoleOf(Guid? id) => id is { } known && roles.TryGetValue(known, out var role) ? role.GetValueOrDefault("code") ?? known.ToString("D") : id?.ToString("D") ?? string.Empty;
        string Scope(IReadOnlyDictionary<string, string?> fields)
        {
            var kind = fields.GetValueOrDefault("scope_kind") ?? Scopes.Tenant;
            var id = Id(fields, "scope_id");
            return kind switch
            {
                Scopes.Tenant => "the whole tenant",
                Scopes.Record => $"record {fields.GetValueOrDefault("scope_record_type")} {id:D}",
                _ => $"{(kind == Scopes.UnitWithChildren ? "unit and below" : "unit")}: {(id is { } unit && units.TryGetValue(unit, out var name) ? name : id?.ToString("D"))}",
            };
        }

        var held = assignments.Values
            .OrderBy(a => Person(Id(a, "principal_id")), StringComparer.Ordinal)
            .ThenBy(a => RoleOf(Id(a, "role_id")), StringComparer.Ordinal)
            .Select(a => (IReadOnlyList<string?>)
            [
                Id(a, "principal_id")?.ToString("D"),
                Person(Id(a, "principal_id")),
                Id(a, "principal_id") is { } principal && people.TryGetValue(principal, out var summary) ? (summary.Type == PrincipalKind.Service ? "service account" : "person") : null,
                RoleOf(Id(a, "role_id")),
                Id(a, "role_id") is { } roleId && roles.TryGetValue(roleId, out var role) ? role.GetValueOrDefault("name") : null,
                Scope(a),
                a.GetValueOrDefault("source"),
            ])
            .ToList();

        var moves = changes
            .Where(c => c.Kind is RecordChangeKinds.Created or RecordChangeKinds.Deleted)
            .Select(c =>
            {
                var fields = c.Fields.ToDictionary(f => f.Field, f => c.Kind == RecordChangeKinds.Created ? f.New : f.Old, StringComparer.Ordinal);
                return (IReadOnlyList<string?>)
                [
                    CanonicalJson.Time(c.At),
                    c.Kind == RecordChangeKinds.Created ? "granted" : "removed",
                    Person(Id(fields, "principal_id")),
                    RoleOf(Id(fields, "role_id")),
                    Scope(fields),
                    Person(c.ActorId),
                    c.ImpersonatorId is { } impersonator ? Person(impersonator) : null,
                    c.Reason,
                ];
            })
            .ToList();

        var roleRows = roles.Values
            .OrderBy(r => r.GetValueOrDefault("code"), StringComparer.Ordinal)
            .Select(r => (IReadOnlyList<string?>)[r.GetValueOrDefault("code"), r.GetValueOrDefault("name"), r.GetValueOrDefault("principal_type"), r.GetValueOrDefault("is_system") == "true" ? "yes" : "no", Permissions(r.GetValueOrDefault("permissions"))])
            .ToList();

        var ruleRows = rules.Values
            .OrderBy(r => r.GetValueOrDefault("name"), StringComparer.Ordinal)
            .Select(r => (IReadOnlyList<string?>)[r.GetValueOrDefault("name"), Side(r, "first", roles), Side(r, "second", roles), r.GetValueOrDefault("mode"), r.GetValueOrDefault("reason"), Person(Id(r, "owner_id"))])
            .ToList();

        var note = "The state at the period's end is today's with every change the field history recorded since undone; the field history began with S3 T7 (ADR-0051).";
        return
        [
            new EvidenceTable(EvidenceDocuments.AccessList, "roles_and_scopes", "Roles and scopes at the period's end",
                [new("principal_id", "Person or account id"), new("name", "Name"), new("type", "Type"), new("role", "Role"), new("role_name", "Role name"), new("scope", "Where it applies"), new("source", "Given by")],
                held, note),
            new EvidenceTable(EvidenceDocuments.AccessList, "assignment_changes", "Roles granted and removed in the period",
                [new("at", "When (UTC)"), new("change", "Change"), new("name", "Person or account"), new("role", "Role"), new("scope", "Where it applies"), new("by", "By"), new("impersonator", "Signed in as them by"), new("reason", "Reason")],
                moves, moves.Count == 0 ? "No role was granted or removed in the period." : null),
            new EvidenceTable(EvidenceDocuments.AccessList, "roles", "Roles and their permissions at the period's end",
                [new("code", "Code"), new("name", "Name"), new("for", "For"), new("system", "System role"), new("permissions", "Permissions")],
                roleRows),
            new EvidenceTable(EvidenceDocuments.SecuritySettings, "conflict_rules", "Segregation of duties rules at the period's end",
                [new("name", "Name"), new("first", "First side"), new("second", "Second side"), new("mode", "Mode"), new("reason", "Reason"), new("owner", "Owner")],
                ruleRows, ruleRows.Count == 0 ? "No segregation of duties rule stood at the period's end." : null),
        ];
    }

    private static Guid? Id(IReadOnlyDictionary<string, string?> fields, string key) => Guid.TryParse(fields.GetValueOrDefault(key), out var id) ? id : null;

    private static string Side(Dictionary<string, string?> rule, string side, IReadOnlyDictionary<Guid, Dictionary<string, string?>> roles)
    {
        var kind = rule.GetValueOrDefault($"{side}_kind");
        var value = rule.GetValueOrDefault($"{side}_value");
        return kind == ConflictSides.Role && Guid.TryParse(value, out var id) && roles.TryGetValue(id, out var role) ? $"role {role.GetValueOrDefault("code")}" : $"{kind} {value}";
    }

    private static string? Permissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
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
