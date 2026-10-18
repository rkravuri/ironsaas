using Iron.Core.Tenancy;
using Iron.Data.Entities;
using Iron.Fleet.Contracts;

namespace Iron.Fleet.Data;

/// <summary>One provisioning run for one tenant (TF-002). Steps and their durations join in T14.</summary>
public sealed class ProvisioningJob : AuditedEntity
{
    private ProvisioningJob()
    {
        TenantSlug = string.Empty;
    }

    public ProvisioningJob(TenantId tenant, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("The tenant is empty.", nameof(tenant));
        }

        TenantSlug = tenant.Value;
        Status = ProvisioningStatus.Requested;
        RequestedAt = now;
    }

    public string TenantSlug { get; private set; }

    public ProvisioningStatus Status { get; private set; }

    /// <summary>The step that runs or failed: <c>database</c>, <c>migrate</c>, ...</summary>
    public string? CurrentStep { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsOpen => Status is ProvisioningStatus.Requested or ProvisioningStatus.Running;

    public void Start(DateTimeOffset now)
    {
        Status = ProvisioningStatus.Running;
        StartedAt ??= now;
        Error = null;
    }

    public void StepStarted(string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        CurrentStep = step;
    }

    public void Fail(DateTimeOffset now, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Status = ProvisioningStatus.Failed;
        Error = error.Length > 2000 ? error[..2000] : error;
        FinishedAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Status = ProvisioningStatus.Completed;
        CurrentStep = null;
        Error = null;
        FinishedAt = now;
    }

    public ProvisioningJobInfo ToInfo() => new(Id, TenantSlug, Status, CurrentStep, Error, RequestedAt, StartedAt, FinishedAt);
}
