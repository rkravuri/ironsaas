namespace Iron.Data.Entities;

/// <summary>
/// A row that is written once and never changed or deleted: audit events, change history, workflow decisions (PRD P8).
/// The table carries the comment <c>append-only</c>, which the schema check reads, and the save interceptor refuses
/// any update or delete.
/// </summary>
public abstract class AppendOnlyEntity : Entity
{
    protected AppendOnlyEntity()
    {
    }

    protected AppendOnlyEntity(Guid id)
        : base(id)
    {
    }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }
}
