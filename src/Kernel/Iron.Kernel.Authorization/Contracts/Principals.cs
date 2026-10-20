namespace Iron.Kernel.Authorization.Contracts;

/// <summary>
/// Who a role is meant for, and what a principal is (TF-032): a person or a service account. Only the tenant
/// administrator system role is for both (ADR-0031); a principal is never <see cref="Any"/>.
/// </summary>
public enum PrincipalKind
{
    User,
    Service,
    Any,
}

/// <summary>
/// What Authorization needs to know of a principal to grant it a role and to show the grant (ADR-0031), and whether it
/// may sign in at all, the first check of an explanation (ADR-0039).
/// </summary>
public sealed record PrincipalSummary(Guid Id, PrincipalKind Type, string DisplayName, Guid? HomeUnitId, string Status = PrincipalStatuses.Active);

/// <summary>A principal's status as Identity keeps it: only an active one signs in.</summary>
public static class PrincipalStatuses
{
    public const string Active = "active";
    public const string Invited = "invited";
    public const string Locked = "locked";
    public const string Disabled = "disabled";
}

/// <summary>
/// The principals of this tenant as Authorization sees them. Identity owns principals and implements this, so the
/// dependency keeps pointing from Identity to Authorization (ADR-0001, ADR-0031).
/// </summary>
public interface IPrincipals
{
    /// <summary>The principals with these ids that exist; an unknown id is left out.</summary>
    Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of the principals whose home unit is one of these: the people an administrator of those units looks
    /// after, such as whose delegations they see (ADR-0037).
    /// </summary>
    Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);
}
