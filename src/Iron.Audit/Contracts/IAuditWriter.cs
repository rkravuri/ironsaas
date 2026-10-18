using Iron.Kernel;

namespace Iron.Audit.Contracts;

/// <summary>
/// What happened, to what, by whom, with what outcome (TF-060). <paramref name="Action"/> is the event type, named by
/// the module that raises it like a permission: <c>authz.permission_denied</c>, <c>identity.login_failed</c>.
/// Details are small key-value pairs, never secrets.
/// </summary>
public sealed record AuditEvent(
    string Action,
    string Outcome,
    Guid PrincipalId,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Details = null,
    string? RecordType = null,
    Guid? RecordId = null,
    string? Reason = null);

public static class AuditOutcome
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failure = "failure";
}

/// <summary>Where an event came from: a person in the shell, a client on the API, or a job (TF-060).</summary>
public static class AuditSource
{
    public const string Ui = "ui";
    public const string Api = "api";
    public const string Job = "job";
}

public static class AuditPermissions
{
    public static readonly Permission EventsRead = new("audit.events.read", "Search and read the audit trail", Risk.Medium);

    public static IReadOnlyList<Permission> All => [EventsRead];
}

/// <summary>
/// Records an audit event as a row in <c>audit.events</c> on the scope's connection: inside the caller's unit of work
/// when one is open, so the event is committed with the change (TF-060 AC1), otherwise at once. Callers never swallow
/// a failure silently: they log it.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
