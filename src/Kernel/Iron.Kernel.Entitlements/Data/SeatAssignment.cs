using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Entitlements.Data;

/// <summary>A person's seat of a per-person module (TF-041). One per module and person.</summary>
public sealed class SeatAssignment : AuditedEntity, ITenantWide
{
    private SeatAssignment()
    {
        ModuleCode = string.Empty;
    }

    public SeatAssignment(string moduleCode, Guid principalId)
    {
        if (!ModuleLicence.IsValidCode(moduleCode))
        {
            throw new ArgumentException($"'{moduleCode}' is not a licence code.", nameof(moduleCode));
        }

        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ModuleCode = moduleCode;
        PrincipalId = principalId;
    }

    /// <summary>The licence code of the module.</summary>
    public string ModuleCode { get; private set; }

    public Guid PrincipalId { get; private set; }
}
