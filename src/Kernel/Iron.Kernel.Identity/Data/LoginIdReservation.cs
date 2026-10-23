using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// A login identifier's tombstone (TF-219, ADR-0051): the identifier stays reserved to the principal it was given to,
/// after they are deactivated and after retention removes them, so it is never given to a second person. Written by the
/// database itself when a principal is inserted (the trigger <c>kernel_identity.reserve_login_id</c>), so no way of
/// creating a person can miss it; never changed or deleted. Its id is the principal's.
/// </summary>
public sealed class LoginIdReservation : AppendOnlyEntity, ITenantWide
{
    private LoginIdReservation()
    {
        LoginId = string.Empty;
    }

    public LoginIdReservation(Guid principalId, string loginId)
        : base(principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        PrincipalId = principalId;
        LoginId = Principal.NormaliseLoginId(loginId);
    }

    public string LoginId { get; private set; }

    public Guid PrincipalId { get; private set; }
}
