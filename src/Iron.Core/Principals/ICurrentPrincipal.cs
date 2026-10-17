namespace Iron.Core.Principals;

/// <summary>
/// Who is acting right now: the authenticated principal of the request, or the system principal in a job.
/// Registered per request by Identity (T10) and as the system principal in workers. The data layer uses it to
/// stamp created_by and updated_by; the audit log uses it for the actor (TF-060).
/// </summary>
public interface ICurrentPrincipal
{
    Guid Id { get; }
}
