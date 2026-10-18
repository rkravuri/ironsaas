using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>The scopes an assignment can have (TF-033). S1 knows the whole tenant; organisation units and records arrive in S2.</summary>
public static class Scopes
{
    public const string Tenant = "tenant";
}

/// <summary>A role given to a principal for a scope (TF-033).</summary>
public sealed class RoleAssignment : AuditedEntity
{
    private RoleAssignment()
    {
        ScopeKind = Scopes.Tenant;
    }

    public RoleAssignment(Guid roleId, Guid principalId)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("The role id is empty.", nameof(roleId));
        }

        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        RoleId = roleId;
        PrincipalId = principalId;
        ScopeKind = Scopes.Tenant;
        ScopeId = null;
    }

    public Guid RoleId { get; private set; }

    public Guid PrincipalId { get; private set; }

    public string ScopeKind { get; private set; }

    /// <summary>The organisation unit or record the assignment is limited to; null for the whole tenant.</summary>
    public Guid? ScopeId { get; private set; }
}
