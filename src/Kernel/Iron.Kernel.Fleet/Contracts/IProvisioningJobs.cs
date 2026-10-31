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

    /// <summary>The operator's link to sign in as a person of the tenant, which allows it (TF-025, ADR-0049): the unit's <c>impersonate</c>.</summary>
    Impersonate,

    /// <summary>A break-glass account approved by a second operator (TF-026, ADR-0049): the unit's <c>break-glass-open</c>.</summary>
    BreakGlassOpen,

    /// <summary>A break-glass account's end and review (TF-026 AC3): the unit's <c>break-glass-close</c>.</summary>
    BreakGlassClose,

    /// <summary>The operator's search or replay of a tenant's domain events, with a reason (TF-112, ADR-0060): the unit's <c>events</c>, whose answer is the job's output.</summary>
    Events,
}

public static class JobKinds
{
    /// <summary>
    /// The operator's commands in an active tenant's unit (ADR-0046, ADR-0049): they change nothing of the tenant's status,
    /// and each runs one image command that hands its result over through the store.
    /// </summary>
    public static bool IsOperatorCommand(this JobKind kind) => kind is JobKind.Recover or JobKind.Impersonate or JobKind.BreakGlassOpen or JobKind.BreakGlassClose or JobKind.Events;
}

/// <summary>
/// What a job was asked for beyond its kind: whom it is about, why, and who asked. A recovery job carries all three; the
/// tenant's audit trail names the operator and the reason (ADR-0046).
/// </summary>
/// <summary>
/// What a job is asked to do beyond its kind: whom it is about, why, and who asked; a recovery job may also reset the
/// administrator's second factors (ADR-0047).
/// </summary>
public sealed record JobRequest(string? Subject, string? Reason, string? RequestedBy, bool ResetFactors = false)
{
    /// <summary>
    /// The configuration the unit's command takes beyond the job's subject and reason, as environment variables
    /// (<c>BreakGlass__Ticket</c>, ...): never a secret, since the job keeps it (ADR-0049).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>To whom the job's handover goes, when not the one who asks for the job: break-glass's link goes to the operator who requested it, not the one who approved it.</summary>
    public Guid? HandOverTo { get; init; }
}

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
public sealed record JobHandover(string Kind, string Subject, string LinkPath, DateTimeOffset ExpiresAt)
{
    /// <summary>What the item carries beyond a link: a break-glass review (ADR-0049).</summary>
    public string? Payload { get; init; }
}

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

    /// <summary>
    /// The control plane collects what a completed job handed over for itself, not for a person: a break-glass review
    /// (ADR-0049). Given once, then deleted; null when there is nothing (yet).
    /// </summary>
    Task<JobHandover?> CollectAsync(Guid jobId, CancellationToken cancellationToken);
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

    /// <summary>One job by its id, with its steps; null when there is none.</summary>
    Task<ProvisioningJobInfo?> FindAsync(Guid jobId, CancellationToken cancellationToken);
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
