using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Creates the system roles and keeps them in step with the permission catalogue (TF-032): the tenant administrator
/// holds every permission of the host, read-only and service-default hold every <c>*.read</c> permission.
/// Safe to run again; it never touches tenant roles.
/// </summary>
public sealed class AuthorizationSeeder(AuthorizationDbContext context, PermissionCatalogue catalogue, ILogger<AuthorizationSeeder> logger) : ISeeder
{
    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var all = catalogue.All.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var reads = all.Where(name => name.EndsWith(".read", StringComparison.Ordinal)).ToList();

        await UpsertAsync(SystemRoles.TenantAdministrator, "Every permission of this tenant.", PrincipalType.User, all, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(SystemRoles.ReadOnly, "Read permissions only.", PrincipalType.User, reads, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(SystemRoles.ServiceDefault, "The default for service accounts: read permissions.", PrincipalType.Service, reads, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(string name, string description, PrincipalType principalType, List<string> permissions, CancellationToken cancellationToken)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Name == name, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            context.Roles.Add(new Role(name, description, isSystem: true, principalType, permissions));
            logger.LogInformation("Created system role {Role} with {Count} permission(s).", name, permissions.Count);
        }
        else if (!role.Permissions.SequenceEqual(permissions, StringComparer.Ordinal))
        {
            role.ReplacePermissions(permissions);
            logger.LogInformation("Updated system role {Role} to {Count} permission(s).", name, permissions.Count);
        }
    }
}
