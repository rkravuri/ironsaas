namespace Iron.Kernel.Data.Entities;

/// <summary>
/// A table the platform's audit verifier may read (TF-061, ADR-0050): the fleet checks a tenant's chain with a role of
/// its own that can read these tables and nothing else, so a unit never verifies itself. <c>migrate</c> grants SELECT on
/// them to <c>Database:AuditReaderRole</c> when it is set.
/// </summary>
public interface IVerifierReadable;
