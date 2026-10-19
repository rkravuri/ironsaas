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

/// <summary>
/// Decides whether a principal holds a permission. Implemented by Iron.Kernel.Authorization from roles and assignments
/// with a short cache (TF-031); any failure must deny. Registered as a scoped service.
/// </summary>
public interface IPermissionEvaluator
{
    ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken);
}
