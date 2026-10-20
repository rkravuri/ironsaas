namespace Iron.Kernel;

/// <summary>
/// Who is acting right now: the authenticated principal of the request, or the system principal in a job.
/// Registered per request by Identity (T10) and as the system principal in workers. The data layer uses it to
/// stamp created_by and updated_by; the audit log uses it for the actor (TF-060).
/// </summary>
public interface ICurrentPrincipal
{
    Guid Id { get; }

    /// <summary>
    /// The person whose delegated duties this principal is acting with, when they chose to act for someone (TF-037,
    /// ADR-0037); null otherwise, and always for a token or a job. The actor stays <see cref="Id"/>: the audit names both.
    /// </summary>
    Guid? ActingForId => null;

    /// <summary>
    /// The unit a person signed in to the shell works for (TF-222, ADR-0038): the one they chose, else their home unit.
    /// New records take it as their owning unit by default, and settings resolve from it. Null for a token or a job.
    /// </summary>
    Guid? WorkingUnitId => null;

    /// <summary>
    /// The consolidated view (TF-222 AC1): lists show everything the person's scopes cover rather than the working unit
    /// and the units below it. Always false for a token or a job, whose lists are never narrowed anyway.
    /// </summary>
    bool AllUnits => false;
}
