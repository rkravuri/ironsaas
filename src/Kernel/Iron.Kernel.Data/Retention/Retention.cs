using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Data.Retention;

/// <summary>
/// How long a kind of record is kept (TF-066, ADR-0050): a default, and the limits a tenant chooses within. The operator
/// may narrow the limits per deployment (<c>Retention:Limits:&lt;code&gt;</c>); a tenant's choice below the minimum is
/// refused. Retention counts from the moment the record's own declaration names: a business document's closure, a
/// session's end, an event's time.
/// </summary>
public sealed record RetentionClass(string Code, string Description, int DefaultDays, int MinimumDays, int MaximumDays);

/// <summary>The platform's retention classes (TF-066). A module puts each record type it owns into one.</summary>
public static class RetentionClasses
{
    private const int Year = 365;

    public static readonly RetentionClass BusinessDocument = new("business_document", "Business documents, counted from their closure", 7 * Year, 1 * Year, 30 * Year);

    public static readonly RetentionClass Session = new("session", "Sign-in sessions, counted from their end", Year, 30, 3 * Year);

    public static readonly RetentionClass SecurityEvent = new("security_event", "A person's security activity", 2 * Year, 90, 10 * Year);

    public static readonly RetentionClass Notification = new("notification", "Notifications sent to people", Year, 30, 3 * Year);

    /// <summary>The audit trail itself: removed from its oldest end only, and only after it was archived (ADR-0050).</summary>
    public static readonly RetentionClass AuditEvent = new("audit_event", "The audit trail, counted from each event's time", 7 * Year, 7 * Year, 30 * Year);

    /// <summary>Not a retention: how long audit events stay in the database before they move to the archive (TF-064 AC2).</summary>
    public static readonly RetentionClass AuditOnline = new("audit_online", "How long audit events stay in the database before the archive", 13 * 30, 30, 10 * Year);

    public static IReadOnlyList<RetentionClass> All => [BusinessDocument, Session, SecurityEvent, Notification, AuditEvent, AuditOnline];
}

/// <summary>
/// One record past its retention as its owner answers it: when its retention began, the master it belongs to when it has
/// one (a supplier for its documents, so a legal hold on the supplier keeps them), and its content for the archive copy.
/// </summary>
public sealed record RetainedRecord(Guid Id, DateTimeOffset RetainedFrom, string? MasterType, Guid? MasterId, string Content);

/// <summary>
/// A module's record type under a retention class (TF-066, ADR-0050): the retention job asks for the records whose
/// retention began before a cutoff, archives each, then has the module delete them, with an audit event, unless a legal
/// hold names the record, its master, or the whole tenant.
/// </summary>
public interface IRetainedRecords
{
    string RecordType { get; }

    RetentionClass Class { get; }

    /// <summary>Records whose retention began before the cutoff, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<RetainedRecord>> ExpiredAsync(DateTimeOffset cutoff, int max, CancellationToken cancellationToken);

    /// <summary>Deletes these records for good, inside the job's unit of work; their archive copies were written first.</summary>
    Task DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}

public static class RetentionServiceCollectionExtensions
{
    /// <summary>Declares a record type's retention; the retention job finds every declaration.</summary>
    public static IServiceCollection AddRetainedRecords<T>(this IServiceCollection services)
        where T : class, IRetainedRecords
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRetainedRecords, T>());
        return services;
    }
}
