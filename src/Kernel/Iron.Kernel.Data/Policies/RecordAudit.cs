using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Policies;

/// <summary>What allowed a reader to see a record (ADR-0038): a scope over its unit, a grant of the record, a share, or the tenant mode.</summary>
public static class VisibleBy
{
    public const string Scope = "scope";
    public const string Record = "record";
    public const string Share = "share";
    public const string Tenant = "tenant";
}

/// <summary>The audit events of business records (ADR-0038), named for the data library as its error codes are.</summary>
public static class RecordAuditEvents
{
    /// <summary>A read by id from outside the reader's home unit; denied when the read was refused.</summary>
    public const string Read = "data.record_read";

    /// <summary>A list page that held records of units outside the reader's home unit.</summary>
    public const string Listed = "data.records_listed";

    /// <summary>A record's sharing changed.</summary>
    public const string SharingChanged = "data.record_sharing_changed";
}

/// <summary>A read by id of a private or shared record from outside the reader's home unit and its subtree.</summary>
public sealed record RecordReadEvent(Guid ReaderId, string RecordType, Guid RecordId, Guid OwningUnitId, string Action, string AllowedBy);

/// <summary>A read by id the policy refused: <c>not_found</c> or <c>forbidden</c>, as the reader was answered.</summary>
public sealed record RecordRefusedEvent(Guid ReaderId, string RecordType, Guid RecordId, Guid OwningUnitId, string Action, string Answer);

/// <summary>One page of a list that held records of other units: their ids and count, one event for the page.</summary>
public sealed record RecordsListedEvent(Guid ReaderId, string RecordType, IReadOnlyList<Guid> RecordIds);

/// <summary>A record's sharing changed: the mode and the listed units, before and after.</summary>
public sealed record RecordSharingEvent(
    Guid ActorId,
    string RecordType,
    Guid RecordId,
    Guid OwningUnitId,
    SharingMode ModeBefore,
    IReadOnlyList<Guid> UnitsBefore,
    SharingMode ModeAfter,
    IReadOnlyList<Guid> UnitsAfter);

/// <summary>
/// Where the data library's audit events go (TF-222 AC2, ADR-0038). The library declares it and Iron.Kernel.Audit
/// implements it, so the library never references a module (ADR-0029). A host without it audits nothing here.
/// </summary>
public interface IRecordAudit
{
    Task ReadAsync(RecordReadEvent read, CancellationToken cancellationToken);

    Task RefusedAsync(RecordRefusedEvent refused, CancellationToken cancellationToken);

    Task ListedAsync(RecordsListedEvent listed, CancellationToken cancellationToken);

    Task SharingChangedAsync(RecordSharingEvent change, CancellationToken cancellationToken);
}

/// <summary>
/// The units that frame a reader (ADR-0038), which Iron.Kernel.Authorization knows from the tree: the home unit with
/// the units below it, outside which a read of a private record is a cross-unit read; and the working unit with the
/// units below it, which lists keep to.
/// </summary>
public interface IUnitReach
{
    /// <summary>The reader's home unit and every unit below it; empty for a principal without a home unit.</summary>
    Task<IReadOnlySet<Guid>> HomeUnitsAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>
    /// The working unit and every unit below it, which lists keep to; null when lists are not narrowed: the consolidated
    /// view, a token, a person acting for someone, or a principal other than the request's.
    /// </summary>
    Task<IReadOnlySet<Guid>?> WorkingUnitsAsync(Guid principalId, CancellationToken cancellationToken);
}
