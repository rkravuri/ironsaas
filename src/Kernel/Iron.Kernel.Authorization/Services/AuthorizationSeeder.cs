using System.Globalization;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Bound from the <c>Seed:RootUnit</c> section: the name and code of the tenant's root unit. The fleet passes the
/// tenant's display name and slug; without them the unit's tenant identifier serves for both.
/// </summary>
public sealed class RootUnitSeedOptions
{
    public const string SectionName = "Seed:RootUnit";

    public string? Name { get; set; }

    public string? Code { get; set; }
}

/// <summary>
/// Creates the system roles and keeps them in step with the permission catalogue (TF-032): the tenant administrator
/// holds every permission of the host and is for any principal type (the control plane's operator is a service account,
/// ADR-0031), read-only and service-default hold every <c>*.read</c> permission, and break-glass every permission but the
/// critical ones (ADR-0049). Then creates
/// the root organisation unit with the provider's root kind, once (TF-034, ADR-0030). Safe to run again; it never
/// touches tenant roles or a root that exists. A system role it changes, as a new module's permissions join it, is on
/// the record as a change by the system, marked when it touches critical access, so the security alert raises it as a
/// privilege grant; and the conflicts report runs at once, so what the new permissions create is listed now rather than
/// the next day (ADR-0056).
/// </summary>
public sealed class AuthorizationSeeder(
    AuthorizationDbContext context,
    PermissionCatalogue catalogue,
    OrganisationUnitService units,
    RootUnitSeedOptions rootUnit,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ConflictService conflicts,
    ILogger<AuthorizationSeeder> logger,
    TenantContext? tenant = null) : ISeeder
{
    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // The definitions are in the contracts, so a host image states its seeds without a database (ADR-0052).
        var changed = 0;
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var role in SystemRoleSeeds.Definitions(catalogue.All))
            {
                changed += await UpsertAsync(role.Code, role.Name, role.Description, role.PrincipalType, [.. role.Permissions], cancellationToken).ConfigureAwait(false) ? 1 : 0;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            var run = await conflicts.RunReportAsync(ConflictReportTriggers.Seed, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The conflicts report ran after the seed changed {Changed} system role(s): {Conflicts} conflict(s).", changed, run.Conflicts);
        }

        var fallback = tenant?.Id.Value;
        var name = string.IsNullOrWhiteSpace(rootUnit.Name) ? fallback ?? "Organisation" : rootUnit.Name;
        var code = string.IsNullOrWhiteSpace(rootUnit.Code) ? fallback ?? "root" : rootUnit.Code;
        await units.EnsureRootAsync(name, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or brings one system role in step; answers whether an existing role changed.</summary>
    private async Task<bool> UpsertAsync(string code, string name, string description, PrincipalKind principalType, List<string> permissions, CancellationToken cancellationToken)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Code == code, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            var created = new Role(code, name, description, isSystem: true, principalType, permissions);
            context.Roles.Add(created);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // A new tenant's system roles are its beginning, not a change someone must look at: on the record, not marked.
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleCreated, AuditOutcome.Success, SystemPrincipal.SystemId, code, Details(created, []), RoleService.RecordType, created.Id, "seed"), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Created system role {Role} with {Count} permission(s).", code, permissions.Count);
            return false;
        }

        if (role.Permissions.SequenceEqual(permissions, StringComparer.Ordinal) && role.Name == name && role.Description == description && role.PrincipalType == principalType)
        {
            return false;
        }

        var before = role.Permissions.ToList();
        role.Change(name, description, principalType, permissions);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = Details(role, before);
        CriticalAccess.Mark(details, catalogue, before, role.Permissions);
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleChanged, AuditOutcome.Success, SystemPrincipal.SystemId, code, details, RoleService.RecordType, role.Id, "seed"), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Updated system role {Role} to {Count} permission(s).", code, permissions.Count);
        return true;
    }

    /// <summary>The details of a role's event as the role service writes them, with what the seed granted and removed.</summary>
    private static Dictionary<string, string> Details(Role role, IReadOnlyList<string> before) => new(StringComparer.Ordinal)
    {
        ["name"] = role.Name,
        ["principal_type"] = role.PrincipalType.ToString().ToLowerInvariant(),
        ["permissions_before"] = string.Join(',', before),
        ["permissions_after"] = string.Join(',', role.Permissions),
        ["granted"] = string.Join(',', role.Permissions.Except(before, StringComparer.Ordinal)),
        ["removed"] = string.Join(',', before.Except(role.Permissions, StringComparer.Ordinal)),
        ["count"] = role.Permissions.Count.ToString(CultureInfo.InvariantCulture),
    };
}
