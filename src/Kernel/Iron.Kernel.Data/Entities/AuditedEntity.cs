namespace Iron.Kernel.Data.Entities;

/// <summary>
/// A row that can change: who created it and when, who changed it last and when, and a version for optimistic
/// concurrency (TF-080). The values are written by <see cref="Iron.Kernel.Data.Conventions.AuditStampInterceptor"/> on
/// every save; code never sets them.
/// </summary>
public abstract class AuditedEntity : Entity
{
    protected AuditedEntity()
    {
    }

    protected AuditedEntity(Guid id)
        : base(id)
    {
    }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    /// <summary>Starts at 1 and grows by one on every saved change. Two people saving the same version: the second save fails.</summary>
    public int Version { get; private set; }
}
