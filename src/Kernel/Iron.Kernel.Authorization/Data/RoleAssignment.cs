using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>The stored names of the assignment scopes (TF-033, ADR-0030).</summary>
public static class Scopes
{
    public const string Tenant = "tenant";
    public const string Unit = "unit";
    public const string UnitWithChildren = "unit_with_children";
    public const string Record = "record";

    public static string NameOf(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.Unit => Unit,
        RoleScopeKind.UnitWithChildren => UnitWithChildren,
        RoleScopeKind.Record => Record,
        _ => Tenant,
    };

    public static RoleScopeKind KindOf(string name) => name switch
    {
        Unit => RoleScopeKind.Unit,
        UnitWithChildren => RoleScopeKind.UnitWithChildren,
        Record => RoleScopeKind.Record,
        _ => RoleScopeKind.Tenant,
    };
}

/// <summary>A role given to a principal for a scope (TF-033): the tenant, a unit with or without its children, or a record.</summary>
public sealed class RoleAssignment : AuditedEntity, ITenantWide
{
    private RoleAssignment()
    {
        ScopeKind = Scopes.Tenant;
    }

    public RoleAssignment(Guid roleId, Guid principalId)
        : this(roleId, principalId, RoleScope.Tenant)
    {
    }

    public RoleAssignment(Guid roleId, Guid principalId, RoleScope scope)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("The role id is empty.", nameof(roleId));
        }

        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentNullException.ThrowIfNull(scope);
        RoleId = roleId;
        PrincipalId = principalId;
        ScopeKind = Scopes.NameOf(scope.Kind);
        ScopeId = scope.UnitId ?? scope.RecordId;
        ScopeRecordType = scope.RecordType;
    }

    public Guid RoleId { get; private set; }

    public Guid PrincipalId { get; private set; }

    public string ScopeKind { get; private set; }

    /// <summary>The organisation unit or record the assignment is limited to; null for the whole tenant.</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>The record type of a record scope; null otherwise.</summary>
    public string? ScopeRecordType { get; private set; }

    /// <summary>The scope as the evaluator and the rules read it.</summary>
    public RoleScope ToScope() => Scopes.KindOf(ScopeKind) switch
    {
        RoleScopeKind.Unit => RoleScope.Unit(ScopeId!.Value),
        RoleScopeKind.UnitWithChildren => RoleScope.Unit(ScopeId!.Value, includeChildren: true),
        RoleScopeKind.Record => RoleScope.Record(ScopeRecordType!, ScopeId!.Value),
        _ => RoleScope.Tenant,
    };
}
