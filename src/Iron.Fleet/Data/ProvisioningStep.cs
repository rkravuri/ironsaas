using Iron.Data.Entities;

namespace Iron.Fleet.Data;

public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>One step of a provisioning job with its timing and outcome (TF-002 AC1: every step is logged with its duration).</summary>
public sealed class ProvisioningStep : AuditedEntity
{
    public const int NameMaxLength = 50;

    private ProvisioningStep()
    {
        Name = string.Empty;
    }

    public ProvisioningStep(Guid jobId, string name, int order)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The job id is empty.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        JobId = jobId;
        Name = name;
        Order = order;
        Status = StepStatus.Pending;
    }

    public Guid JobId { get; private set; }

    public string Name { get; private set; }

    public int Order { get; private set; }

    public StepStatus Status { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public int? DurationMs { get; private set; }

    /// <summary>What the step reports when it is done: the hostnames reserved, the invitation link, ...</summary>
    public string? Output { get; private set; }

    public string? Error { get; private set; }

    public void Start(DateTimeOffset now)
    {
        Status = StepStatus.Running;
        StartedAt = now;
        FinishedAt = null;
        DurationMs = null;
        Error = null;
    }

    public void Complete(DateTimeOffset now, string? output)
    {
        Status = StepStatus.Completed;
        Output = Trim(output, 2000);
        Finish(now);
    }

    public void Fail(DateTimeOffset now, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Status = StepStatus.Failed;
        Error = Trim(error, 2000);
        Finish(now);
    }

    private void Finish(DateTimeOffset now)
    {
        FinishedAt = now;
        DurationMs = StartedAt is null ? null : (int)Math.Max(0, (now - StartedAt.Value).TotalMilliseconds);
    }

    private static string? Trim(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
}
