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

    /// <summary>
    /// An administrator's action on the tenant's people, access or configuration (TF-067, ADR-0052): what an evidence
    /// pack lists as the period's administrative actions. Declared with <see cref="AsAdministrative"/>.
    /// </summary>
    public bool Administrative { get; private init; }

    /// <summary>The same type, marked as an administrative action.</summary>
    public AuditEventType AsAdministrative() => this with { Administrative = true };
}
