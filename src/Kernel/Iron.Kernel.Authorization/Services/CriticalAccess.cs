using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Marks a role's event as touching critical access (ADR-0056): a role that holds or held a critical permission was
/// created, changed or deleted, or given to or taken from someone. The security alert for configuration changes in
/// critical areas counts the events so marked; the details already list the permissions, but a rule cannot weigh them.
/// </summary>
public static class CriticalAccess
{
    public static void Mark(IDictionary<string, string> details, PermissionCatalogue catalogue, params IEnumerable<string>[] permissions)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.SelectMany(set => set).Any(name => catalogue.Find(name)?.Risk is Risk.Critical))
        {
            details[AuditDetails.Critical] = AuditDetails.True;
        }
    }
}
