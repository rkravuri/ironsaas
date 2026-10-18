namespace Iron.Audit.Contracts;

/// <summary>What happened, to what, by whom, with what outcome (TF-060). Details are small key-value pairs, never secrets.</summary>
public sealed record AuditEvent(
    string Action,
    string Outcome,
    Guid PrincipalId,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Details = null);

public static class AuditOutcome
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failure = "failure";
}

/// <summary>
/// Records an audit event. Iron.Audit stores it in <c>audit.events</c> on the caller's connection and transaction (T9);
/// until then it writes a structured log line. Callers never swallow a failure silently: they log it.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
