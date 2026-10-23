namespace Iron.Kernel.History;

/// <summary>
/// An audit event type and the words it reads as (ADR-0051). The audit trail, the History tab and the exports show the
/// name beside the code. A module declares one for every event it writes (<c>IModule.AuditEventTypes</c>), next to the
/// event's constant; a test fails for a constant without its name.
/// </summary>
public sealed record AuditEventType
{
    public AuditEventType(string code, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name;
    }

    /// <summary>The code events carry: the module's name, a dot and what happened, such as <c>identity.login_failed</c>.</summary>
    public string Code { get; }

    /// <summary>The words a reader sees, in sentence case: "Sign-in failed".</summary>
    public string Name { get; }
}
