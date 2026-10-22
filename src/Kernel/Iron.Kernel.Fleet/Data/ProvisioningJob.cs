using Iron.Kernel.Data.Entities;
using Iron.Kernel.Fleet.Contracts;

namespace Iron.Kernel.Fleet.Data;

/// <summary>One provisioning run for one tenant (TF-002), with its steps.</summary>
public sealed class ProvisioningJob : AuditedEntity
{
    private ProvisioningJob()
    {
        TenantSlug = string.Empty;
    }

    public const int SubjectMaxLength = 254;
    public const int ReasonMaxLength = 500;
    public const int RequestedByMaxLength = 254;

    public ProvisioningJob(TenantId tenant, JobKind kind, DateTimeOffset now, JobRequest? request = null, Guid? requestedById = null)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("The tenant is empty.", nameof(tenant));
        }

        TenantSlug = tenant.Value;
        Kind = kind;
        Status = ProvisioningStatus.Requested;
        RequestedAt = now;
        Subject = Cut(request?.Subject, SubjectMaxLength);
        Reason = Cut(request?.Reason, ReasonMaxLength);
        RequestedBy = Cut(request?.RequestedBy, RequestedByMaxLength);
        ResetFactors = request?.ResetFactors ?? false;
        RequestedById = requestedById;
        Environment = request?.Environment is { Count: > 0 } environment ? System.Text.Json.JsonSerializer.Serialize(environment) : null;
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

    /// <summary>What the last step reported, for the operator. Never a link or a code: those are handed over (ADR-0046).</summary>
    public string? Output { get; private set; }

    /// <summary>Whom the job is about: the administrator a recovery job recovers.</summary>
    public string? Subject { get; private set; }

    /// <summary>Why it was asked for, in the operator's words; a recovery job always has one.</summary>
    public string? Reason { get; private set; }

    /// <summary>Who asked, as the control plane names them.</summary>
    public string? RequestedBy { get; private set; }

    /// <summary>A recovery job that also removes the administrator's second factors (TF-013 AC3, ADR-0047).</summary>
    public bool ResetFactors { get; private set; }

    /// <summary>The principal who asked: the only one the job's handover is given to (ADR-0046). Null for a job from before.</summary>
    public Guid? RequestedById { get; private set; }

    /// <summary>When the job's handover was given to the one who asked; it is not given again.</summary>
    public DateTimeOffset? HandedOverAt { get; private set; }

    /// <summary>The configuration of an operator's command beyond its subject and reason, as JSON (ADR-0049); never a secret.</summary>
    public string? Environment { get; private set; }

    public JobRequest? Request => Subject is null && Reason is null && RequestedBy is null
        ? null
        : new JobRequest(Subject, Reason, RequestedBy, ResetFactors) { Environment = Environment is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Environment) };

    public bool IsOpen => Status is ProvisioningStatus.Requested or ProvisioningStatus.Running;

    public void HandedOver(DateTimeOffset now) => HandedOverAt = now;

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
        (steps ?? []).Select(s => new ProvisioningStepInfo(s.Name, s.Order, s.Status.ToString().ToLowerInvariant(), s.StartedAt, s.FinishedAt, s.DurationMs, s.Output, s.Error)).ToList(),
        Request,
        RequestedById,
        HandedOverAt);

    private static string? Cut(string? value, int length) => value is { Length: > 0 } && value.Length > length ? value[..length] : value;
}
