using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Seeding;
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
/// touches tenant roles or a root that exists.
/// </summary>
public sealed class AuthorizationSeeder(
    AuthorizationDbContext context,
    PermissionCatalogue catalogue,
    OrganisationUnitService units,
    RootUnitSeedOptions rootUnit,
    ILogger<AuthorizationSeeder> logger,
    TenantContext? tenant = null) : ISeeder
{
    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // The definitions are in the contracts, so a host image states its seeds without a database (ADR-0052).
        foreach (var role in SystemRoleSeeds.Definitions(catalogue.All))
        {
            await UpsertAsync(role.Code, role.Name, role.Description, role.PrincipalType, [.. role.Permissions], cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var fallback = tenant?.Id.Value;
        var name = string.IsNullOrWhiteSpace(rootUnit.Name) ? fallback ?? "Organisation" : rootUnit.Name;
        var code = string.IsNullOrWhiteSpace(rootUnit.Code) ? fallback ?? "root" : rootUnit.Code;
        await units.EnsureRootAsync(name, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(string code, string name, string description, PrincipalKind principalType, List<string> permissions, CancellationToken cancellationToken)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Code == code, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            context.Roles.Add(new Role(code, name, description, isSystem: true, principalType, permissions));
            logger.LogInformation("Created system role {Role} with {Count} permission(s).", code, permissions.Count);
        }
        else if (!role.Permissions.SequenceEqual(permissions, StringComparer.Ordinal) || role.Name != name || role.Description != description || role.PrincipalType != principalType)
        {
            role.Change(name, description, principalType, permissions);
            logger.LogInformation("Updated system role {Role} to {Count} permission(s).", code, permissions.Count);
        }
    }
}
