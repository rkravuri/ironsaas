namespace Iron.Kernel.Data;

/// <summary>Bound by the host from the <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>The runtime role of this unit, for example <c>acme_app</c>. The fleet sets it; <c>migrate</c> grants its privileges when it is set.</summary>
    public string? AppRole { get; set; }

    /// <summary>
    /// The audit verifier's role of this unit, for example <c>acme_audit</c> (ADR-0050): the fleet reads the chain with
    /// it, and <c>migrate</c> grants it SELECT on the chain's tables alone when it is set.
    /// </summary>
    public string? AuditReaderRole { get; set; }
}
