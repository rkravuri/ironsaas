namespace Iron.Kernel.Api.Permissions;

/// <summary>
/// One permission check: who, what, where (the scope, tenant-wide unless the endpoint declares one), and the operation
/// and path for the audit trail.
/// </summary>
public sealed record AccessRequest(Guid PrincipalId, Permission Permission, string Operation, string Path)
{
    public AccessScope Scope { get; init; } = AccessScope.Tenant;
}

/// <summary>The answer of a permission check. Every denial has a reason for the log and the audit event.</summary>
public sealed record AccessDecision(bool Allowed, string Reason)
{
    public const string Granted = "granted";
    public const string NotGranted = "not_granted";
    public const string Unavailable = "unavailable";
    public const string NoEvaluator = "no_evaluator";

    public static readonly AccessDecision Allow = new(true, Granted);

    public static AccessDecision Deny(string reason) => new(false, reason);
}

/// <summary>An assignment that grants a permission at a scope: what an explanation names for an allowance (TF-039 AC2).</summary>
public sealed record GrantingAssignment(Guid AssignmentId, Guid RoleId, string RoleCode, string RoleName, string ScopeKind, Guid? ScopeId, string? RecordType);

/// <summary>
/// A permission check explained (ADR-0039): the answer <see cref="IPermissionEvaluator.EvaluateAsync"/> gives, the
/// assignments that granted the permission at the scope, and whose grants counted: the delegator's, limited to the
/// permissions of the delegated duties, while the principal acts for someone (ADR-0037).
/// </summary>
public sealed record AccessExplanation(bool Allowed, string Reason, IReadOnlyList<GrantingAssignment> GrantedBy, Guid? ActingForId = null, IReadOnlyCollection<string>? ActingLimit = null)
{
    public static AccessExplanation From(AccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new AccessExplanation(decision.Allowed, decision.Reason, []);
    }
}

/// <summary>
/// Decides whether a principal holds a permission. Implemented by Iron.Kernel.Authorization from roles and assignments
/// with a short cache (TF-031); any failure must deny. Registered as a scoped service.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>The answer the filters act on; a denial is logged, counted and audited.</summary>
    ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken);

    /// <summary>The same answer with what decided it, and without the side effects of a denial (ADR-0039).</summary>
    async ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken cancellationToken) =>
        AccessExplanation.From(await EvaluateAsync(request, cancellationToken).ConfigureAwait(false));
}
