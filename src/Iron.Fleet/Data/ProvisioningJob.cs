using Iron.Core.Tenancy;
using Iron.Data.Entities;
using Iron.Fleet.Contracts;

namespace Iron.Fleet.Data;

/// <summary>One provisioning run for one tenant (TF-002), with its steps.</summary>
public sealed class ProvisioningJob : AuditedEntity
{
    private ProvisioningJob()
    {
        TenantSlug = string.Empty;
    }

    public ProvisioningJob(TenantId tenant, JobKind kind, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("The tenant is empty.", nameof(tenant));
        }

        TenantSlug = tenant.Value;
        Kind = kind;
        Status = ProvisioningStatus.Requested;
        RequestedAt = now;
    }

    public string TenantSlug { get; private set; }

    public JobKind Kind { get; private set; }

    public ProvisioningStatus Status { get; private set; }

    /// <summary>The step that runs or failed: <c>database</c>, <c>migrate</c>, ...</summary>
    public string? CurrentStep { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>What the operator needs when the job is done: the invitation link of the first administrator.</summary>
    public string? Output { get; private set; }

    public bool IsOpen => Status is ProvisioningStatus.Requested or ProvisioningStatus.Running;

    /// <summary>A Failed job is asked to run again: it continues from the failed step (TF-002 AC2).</summary>
    public void Reopen(DateTimeOffset now)
    {
        Status = ProvisioningStatus.Requested;
        RequestedAt = now;
        FinishedAt = null;
        Error = null;
    }

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

    public void Complete(DateTimeOffset now, string? output)
    {
        Status = ProvisioningStatus.Completed;
        CurrentStep = null;
        Error = null;
        Output = output is { Length: > 2000 } ? output[..2000] : output;
        FinishedAt = now;
    }

    public ProvisioningJobInfo ToInfo(IReadOnlyList<ProvisioningStep>? steps = null) => new(
        Id,
        TenantSlug,
        Kind,
        Status,
        CurrentStep,
        Error,
        Output,
        RequestedAt,
        StartedAt,
        FinishedAt,
        (steps ?? []).Select(s => new ProvisioningStepInfo(s.Name, s.Order, s.Status.ToString().ToLowerInvariant(), s.StartedAt, s.FinishedAt, s.DurationMs, s.Output, s.Error)).ToList());
}
