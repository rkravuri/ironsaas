namespace Iron.Kernel.Fleet.Contracts;

/// <summary>Where a provisioning job stands (TF-002). The fleet worker moves it (T14); the control plane shows it.</summary>
public enum ProvisioningStatus
{
    Requested,
    Running,
    Failed,
    Completed,
}

/// <summary>What a job does: bring a unit up, or take it down while keeping its data (TF-210, thin de-provision in S1).</summary>
public enum JobKind
{
    Provision,
    Deprovision,
    /// <summary>Re-render and re-apply the unit of an Active tenant: a new image tag, a new template, a new hostname.</summary>
    Update,

    /// <summary>
    /// A recovery link for a tenant administrator, at the operator's request with a reason (TF-017, ADR-0046): the unit's
    /// <c>recover-admin</c> command issues it, and the job's output is the link. The tenant's status does not change.
    /// </summary>
    Recover,
}

/// <summary>
/// What a job was asked for beyond its kind: whom it is about, why, and who asked. A recovery job carries all three; the
/// tenant's audit trail names the operator and the reason (ADR-0046).
/// </summary>
public sealed record JobRequest(string? Subject, string? Reason, string? RequestedBy);

public sealed record ProvisioningStepInfo(string Name, int Order, string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? DurationMs, string? Output, string? Error);

public sealed record ProvisioningJobInfo(
    Guid Id,
    string TenantSlug,
    JobKind Kind,
    ProvisioningStatus Status,
    string? CurrentStep,
    string? Error,
    string? Output,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ProvisioningStepInfo> Steps,
    JobRequest? Request = null,
    Guid? RequestedById = null,
    DateTimeOffset? HandedOverAt = null);

/// <summary>What a job's command handed over to the operator (ADR-0046): an invitation or a recovery link, whose, and until when.</summary>
public sealed record JobHandover(string Kind, string Subject, string LinkPath, DateTimeOffset ExpiresAt);

/// <summary>Where a job's handover stands for the one who asks.</summary>
public enum HandoverState
{
    /// <summary>The job handed nothing over, or has not finished.</summary>
    None,

    /// <summary>Given now, once: the place is deleted.</summary>
    Given,

    /// <summary>Given earlier; it is not shown again.</summary>
    Taken,

    /// <summary>Its link stopped working before anyone took it; the place is deleted.</summary>
    Expired,

    /// <summary>Waiting for the operator who asked for the job; someone else asked.</summary>
    NotYours,
}

public sealed record JobHandoverAnswer(HandoverState State, JobHandover? Item = null);

/// <summary>
/// A job's handover (ADR-0046): the item its command wrote to the job's one-time place in the store, given once to the
/// principal who asked for the job and then deleted. It never travels through the job's output, a log line or a trace.
/// </summary>
public interface IJobHandovers
{
    Task<JobHandoverAnswer> TakeAsync(Guid jobId, Guid askerId, CancellationToken cancellationToken);
}

/// <summary>
/// Provisioning jobs as the control plane uses them: one request per tenant at a time; the fleet worker picks up
/// Requested jobs and runs the steps (T14). A tenant that is Active is refused by the caller before a job is made.
/// </summary>
public interface IProvisioningJobs
{
    /// <summary>
    /// Creates a Requested job, reopens the last one when it Failed (so the run continues from the failed step), or fails
    /// with <c>fleet.job.in_progress</c> while one is Requested or Running.
    /// </summary>
    Task<Result<ProvisioningJobInfo>> RequestAsync(string tenantSlug, JobKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="RequestAsync(string, JobKind, CancellationToken)"/>, with what the job is for. A job with a request is
    /// never reopened: a new request may have a new reason, so it gets a new job.
    /// </summary>
    Task<Result<ProvisioningJobInfo>> RequestAsync(string tenantSlug, JobKind kind, JobRequest? request, CancellationToken cancellationToken);

    Task<ProvisioningJobInfo?> LatestAsync(string tenantSlug, CancellationToken cancellationToken);
}

public static class FleetPermissions
{
    public static readonly Permission JobsRead = new("fleet.jobs.read", "View provisioning jobs and their steps", Risk.Low);

    public static IReadOnlyList<Permission> All => [JobsRead];
}

public static class FleetErrors
{
    public const string JobInProgress = "fleet.job.in_progress";
}
