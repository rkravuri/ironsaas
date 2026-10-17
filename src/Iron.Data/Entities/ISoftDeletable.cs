namespace Iron.Data.Entities;

/// <summary>
/// A row that is never physically deleted (PRD P8). Deleting it through the context marks it instead, and a global
/// query filter hides marked rows; <c>IgnoreQueryFilters()</c> shows them again. Implement with private setters.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAt { get; }

    Guid? DeletedBy { get; }
}
