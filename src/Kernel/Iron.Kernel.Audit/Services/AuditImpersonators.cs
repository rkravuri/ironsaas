using System.Text.Json;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Someone the audit search's "signed in as them by" filter offers (TF-025, TF-064): an operator of the platform or a
/// person of the tenant who signed in as someone, found by <paramref name="Field"/> <c>impersonator_id</c>; or a
/// break-glass account, which acts as itself and is found by <c>actor_id</c>.
/// </summary>
public sealed record SignedInAs(Guid Id, string Name, string Kind, string Field);

public static class SignedInAsKinds
{
    /// <summary>The platform's operator, nobody in the unit: named by the trail's own record of them.</summary>
    public const string Operator = "operator";

    /// <summary>An account break-glass opened for two operators (ADR-0049).</summary>
    public const string BreakGlass = "break_glass";

    /// <summary>A person of the tenant who signed in as another.</summary>
    public const string Person = "person";
}

/// <summary>
/// Everyone who entered the trail as someone else or from outside the tenant, by name (TF-025 AC2, ADR-0049): every
/// impersonator the trail names, the tenant's people by their directory name and the operator by the name the writer
/// kept with the event, and every break-glass account by its name. Operators first, then break-glass accounts, then people.
/// </summary>
public sealed class AuditImpersonators(AuditDbContext context, IServiceProvider services)
{
    /// <summary>
    /// The event Identity writes when break-glass opens an account. Identity sits above this module, so the audit trail
    /// knows the event by its name, which is part of the trail's record and does not change.
    /// </summary>
    public const string BreakGlassOpened = "identity.break_glass_opened";

    public async Task<IReadOnlyList<SignedInAs>> ListAsync(CancellationToken cancellationToken)
    {
        var impersonators = await context.Events.AsNoTracking()
            .Where(e => e.ImpersonatorId != null)
            .Select(e => e.ImpersonatorId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var accounts = await context.Events.AsNoTracking()
            .Where(e => e.EventType == BreakGlassOpened && e.RecordId != null)
            .Select(e => new { Id = e.RecordId!.Value, e.Target })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var principals = services.GetService<IPrincipals>();
        var known = principals is null
            ? new Dictionary<Guid, string>()
            : (await principals.FindAsync([.. impersonators, .. accounts.Select(a => a.Id)], cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value.DisplayName);

        var found = new List<SignedInAs>();
        foreach (var id in impersonators)
        {
            if (known.TryGetValue(id, out var person))
            {
                found.Add(new SignedInAs(id, person, SignedInAsKinds.Person, "impersonator_id"));
                continue;
            }

            // The operator is nobody in the unit: the writer kept their name with every event they made here.
            var details = await context.Events.AsNoTracking()
                .Where(e => e.ImpersonatorId == id && e.Details != null)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Details)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            found.Add(new SignedInAs(id, NameIn(details) ?? id.ToString("D"), SignedInAsKinds.Operator, "impersonator_id"));
        }

        found.AddRange(accounts.Select(a => new SignedInAs(a.Id, known.GetValueOrDefault(a.Id) ?? a.Target ?? a.Id.ToString("D"), SignedInAsKinds.BreakGlass, "actor_id")));
        string[] order = [SignedInAsKinds.Operator, SignedInAsKinds.BreakGlass, SignedInAsKinds.Person];
        return [.. found.DistinctBy(s => (s.Id, s.Kind)).OrderBy(s => Array.IndexOf(order, s.Kind)).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string? NameIn(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        using var document = JsonDocument.Parse(details);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(AuditWriter.ImpersonatorKey, out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
    }
}
