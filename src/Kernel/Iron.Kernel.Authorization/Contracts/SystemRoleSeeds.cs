namespace Iron.Kernel.Authorization.Contracts;

/// <summary>A system role as the seed makes it: its code, name, description, principal type and permissions.</summary>
public sealed record SystemRoleDefinition(string Code, string Name, string Description, PrincipalKind PrincipalType, IReadOnlyList<string> Permissions);

/// <summary>
/// The system roles and their permissions as the seed makes them from a permission catalogue (TF-032): the tenant
/// administrator holds every permission and is for any principal type (the control plane's operator is a service account,
/// ADR-0031), read-only and service-default hold every <c>*.read</c> permission, break-glass every permission but the
/// critical ones (ADR-0049), and the unit client those marked for unit clients. Public, so a host image can state its
/// seeds without a database for the installation record (ADR-0052).
/// </summary>
public static class SystemRoleSeeds
{
    public static IReadOnlyList<SystemRoleDefinition> Definitions(IReadOnlyList<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var all = permissions.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var reads = all.Where(name => name.EndsWith(".read", StringComparison.Ordinal)).ToList();
        var notCritical = permissions.Where(p => p.Risk != Risk.Critical).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var forUnits = permissions.Where(p => p.ForUnitClients).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        List<SystemRoleDefinition> roles =
        [
            new(SystemRoles.TenantAdministrator, "Tenant administrator", "Every permission of this tenant.", PrincipalKind.Any, all),
            new(SystemRoles.ReadOnly, "Read only", "Read permissions only.", PrincipalKind.User, reads),
            new(SystemRoles.ServiceDefault, "Service default", "The default for service accounts: read permissions.", PrincipalKind.Service, reads),
            new(SystemRoles.BreakGlass, "Break-glass", "An operator's emergency access, for a limited time: every permission of this tenant but the critical ones.", PrincipalKind.User, notCritical),
        ];
        if (forUnits.Count > 0)
        {
            roles.Add(new(SystemRoles.UnitClient, "Unit client", "A tenant unit's own client in the control plane: it sends the unit's audit anchors and reads its chain's state, nothing else.", PrincipalKind.Service, forUnits));
        }

        return roles;
    }

    /// <summary>
    /// The seeds' canonical text (ADR-0052): one line per system role, by code, <c>code:permission,permission</c>, the
    /// permissions in order. A release hashes it, and the installation record hashes the same lines read from a tenant's
    /// database, so an installation is checked against the code that seeded it.
    /// </summary>
    public static string Text(IEnumerable<(string Code, IEnumerable<string> Permissions)> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return string.Concat(roles.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => $"{r.Code}:{string.Join(',', r.Permissions.Order(StringComparer.Ordinal))}\n"));
    }
}
