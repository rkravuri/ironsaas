using Iron.Authorization.Contracts;
using Iron.Data.Seeding;
using Iron.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Identity.Services;

/// <summary>Bound from the <c>Seed:Administrator</c> section: the first administrator of a new tenant (S1 plan, provisioning step 4).</summary>
public sealed class AdministratorSeedOptions
{
    public const string SectionName = "Seed:Administrator";

    public string? LoginId { get; set; }

    public string? DisplayName { get; set; }
}

/// <summary>
/// Creates the first administrator in Invited status with an invitation token and the tenant-administrator role.
/// Runs after the Authorization seeder (order 100). Without <c>Seed:Administrator:LoginId</c> it does nothing. The
/// invitation token is written to the log once, at creation, because no email leaves a unit in S1; the fleet prints
/// the link (T14). Running again changes nothing.
/// </summary>
public sealed class IdentitySeeder(
    IdentityDbContext context,
    AdministratorSeedOptions options,
    InvitationService invitations,
    IRoleAssignments roleAssignments,
    ILogger<IdentitySeeder> logger) : ISeeder
{
    public int Order => 200;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var loginId = Principal.NormaliseLoginId(options.LoginId);
        if (loginId.Length == 0)
        {
            logger.LogWarning("No first administrator is configured (Seed:Administrator:LoginId); none was created.");
            return;
        }

        var existing = await context.Principals.SingleOrDefaultAsync(p => p.LoginId == loginId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            logger.LogInformation("The administrator {LoginId} exists already; nothing to seed.", loginId);
            return;
        }

        var administrator = new Principal(loginId, PrincipalType.User, options.DisplayName ?? "Administrator", loginId.Contains('@') ? loginId : null);
        context.Principals.Add(administrator);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var token = await invitations.InviteAsync(administrator, cancellationToken).ConfigureAwait(false);
        var assigned = await roleAssignments.AssignAsync(administrator.Id, SystemRoles.TenantAdministrator, cancellationToken).ConfigureAwait(false);
        if (assigned.IsFailure)
        {
            throw new InvalidOperationException($"The tenant-administrator role could not be assigned: {assigned.Error.Message}. Run the Authorization seeder first.");
        }

        logger.LogInformation("Created the first administrator {LoginId} ({PrincipalId}) in Invited status. Invitation token: {Token}", loginId, administrator.Id, token);
    }
}
