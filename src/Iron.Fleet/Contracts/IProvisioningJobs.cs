using Iron.Core.Permissions;
using Iron.Core.Results;

namespace Iron.Fleet.Contracts;

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
    IReadOnlyList<ProvisioningStepInfo> Steps);

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
