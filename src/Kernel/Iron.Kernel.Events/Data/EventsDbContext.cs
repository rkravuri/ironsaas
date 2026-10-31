using System.Text.Json;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Events.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Events.Data;

/// <summary>
/// A domain event in the outbox (TF-085, ADR-0060), written in the transaction of the change that raised it: its place
/// in the outbox, what it is, the record it is about, who acted, when, the request's correlation id, and its data, which
/// carries identifiers only. The dispatcher marks it handed on once every subscriber has its row in the inbox.
/// </summary>
public sealed class OutboxEvent : AuditedEntity, ITenantWide
{
    public const int TypeMaxLength = 200;
    public const int RecordTypeMaxLength = 60;
    public const int TenantMaxLength = 63;
    public const int CorrelationMaxLength = 64;

    private OutboxEvent()
    {
        Type = string.Empty;
        RecordType = string.Empty;
        Tenant = string.Empty;
        Data = "{}";
    }

    /// <summary>The id is a UUID version 7 of the event's own time, so an id tells when its event happened, archived or not.</summary>
    public OutboxEvent(string type, int version, string recordType, Guid recordId, string tenant, Guid actorId, Guid? onBehalfOfId, DateTimeOffset occurredAt, string? correlationId, string data)
        : base(Ids.At(occurredAt))
    {
        Type = type;
        EventVersion = version;
        RecordType = recordType;
        RecordId = recordId;
        Tenant = tenant;
        ActorId = actorId;
        OnBehalfOfId = onBehalfOfId;
        OccurredAt = occurredAt;
        CorrelationId = correlationId is { Length: > CorrelationMaxLength } tooLong ? tooLong[..CorrelationMaxLength] : correlationId;
        Data = data;
    }

    /// <summary>The order the events were written in, which the dispatcher keeps per record.</summary>
    public long Position { get; private set; }

    public string Type { get; private set; }

    /// <summary>The event's version, beside the row's own <c>Version</c>, its concurrency token.</summary>
    public int EventVersion { get; private set; }

    public string RecordType { get; private set; }

    public Guid RecordId { get; private set; }

    public string Tenant { get; private set; }

    public Guid ActorId { get; private set; }

    public Guid? OnBehalfOfId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>The data, as the declaration's JSON.</summary>
    public string Data { get; private set; }

    /// <summary>When the dispatcher gave every subscriber its row in the inbox; null until then.</summary>
    public DateTimeOffset? FannedAt { get; private set; }

    public void Fan(DateTimeOffset at) => FannedAt = at;

    public EventEnvelope ToEnvelope()
    {
        using var document = JsonDocument.Parse(Data);
        return new EventEnvelope(Id, Position, Type, EventVersion, RecordType, RecordId, Tenant, ActorId, OnBehalfOfId, OccurredAt, CorrelationId, document.RootElement.Clone());
    }
}

/// <summary>
/// One subscriber's record of one event (TF-085, TF-111, ADR-0060): the inbox. Pending until the subscriber's handler ran
/// and committed with this row marked handled, so a repeated delivery finds it handled and changes nothing; a failure
/// counts an attempt and waits longer each time; after the last attempt, a dead letter with its error. A delivery to a
/// subscriber of a suite the unit no longer runs is held until the suite returns.
/// </summary>
public sealed class InboxEntry : AuditedEntity, ITenantWide
{
    public const int SubscriberMaxLength = EventSubscription.MaxNameLength;
    public const int ErrorMaxLength = 1000;

    private InboxEntry()
    {
        Subscriber = string.Empty;
        RecordType = string.Empty;
        Status = DeliveryStates.Pending;
    }

    public InboxEntry(string subscriber, Guid eventId, long position, string recordType, Guid recordId, DateTimeOffset dueAt, string? suite = null)
        : this()
    {
        Subscriber = subscriber;
        EventId = eventId;
        Position = position;
        RecordType = recordType;
        RecordId = recordId;
        NextAttemptAt = dueAt;
        Suite = suite;
    }

    public string Subscriber { get; private set; }

    /// <summary>The suite of the subscriber's module, or null for the Foundation's: a removed suite's deliveries are held by it.</summary>
    public string? Suite { get; private set; }

    /// <summary>When the delivery was held because its suite was removed; null unless it is held.</summary>
    public DateTimeOffset? HeldAt { get; private set; }

    public Guid EventId { get; private set; }

    /// <summary>The event's place in the outbox, for the order per record.</summary>
    public long Position { get; private set; }

    public string RecordType { get; private set; }

    public Guid RecordId { get; private set; }

    public string Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? HandledAt { get; private set; }

    public DateTimeOffset? DeadAt { get; private set; }

    /// <summary>Replays, by a person or the operator, since it was first written.</summary>
    public int Replays { get; private set; }

    public void Handled(DateTimeOffset at)
    {
        Status = DeliveryStates.Handled;
        Attempts++;
        HandledAt = at;
    }

    /// <summary>A failed attempt: retried at <paramref name="retryAt"/>, or, when it was the last, parked as a dead letter.</summary>
    public void Failed(DateTimeOffset at, string error, DateTimeOffset? retryAt)
    {
        Attempts++;
        LastError = error.Length <= ErrorMaxLength ? error : error[..(ErrorMaxLength - 1)] + "…";
        if (retryAt is { } next)
        {
            NextAttemptAt = next;
            return;
        }

        Status = DeliveryStates.Dead;
        DeadAt = at;
    }

    /// <summary>
    /// Held, because the unit no longer runs the subscriber's suite: neither tried, nor counted, nor a dead letter, until the
    /// suite returns (ADR-0060). Only a pending delivery is held.
    /// </summary>
    public bool Hold(DateTimeOffset at)
    {
        if (Status != DeliveryStates.Pending)
        {
            return false;
        }

        Status = DeliveryStates.Held;
        HeldAt = at;
        return true;
    }

    /// <summary>Pending again, at once, because the subscriber's suite is back; its attempts are as they were when it was held.</summary>
    public bool Resume(DateTimeOffset at)
    {
        if (Status != DeliveryStates.Held)
        {
            return false;
        }

        Status = DeliveryStates.Pending;
        HeldAt = null;
        NextAttemptAt = at;
        return true;
    }

    /// <summary>Back to pending for a replay; a handled entry stays handled, so a replay never runs a handler twice on an event.</summary>
    public bool Requeue(DateTimeOffset at)
    {
        if (Status == DeliveryStates.Handled)
        {
            return false;
        }

        Status = DeliveryStates.Pending;
        Attempts = 0;
        NextAttemptAt = at;
        DeadAt = null;
        Replays++;
        return true;
    }
}

/// <summary>
/// A batch of events past the retention, archived to the tenant's container and removed from the outbox (TF-112 AC2): the
/// positions and times it covers, how many, and the object it was written to.
/// </summary>
public sealed class EventArchive : AppendOnlyEntity, ITenantWide
{
    public const int KeyMaxLength = 200;

    private EventArchive()
    {
        ObjectKey = string.Empty;
    }

    public EventArchive(long fromPosition, long toPosition, DateTimeOffset fromTime, DateTimeOffset toTime, int count, string objectKey)
        : this()
    {
        FromPosition = fromPosition;
        ToPosition = toPosition;
        FromTime = fromTime;
        ToTime = toTime;
        Count = count;
        ObjectKey = objectKey;
    }

    public long FromPosition { get; private set; }

    public long ToPosition { get; private set; }

    public DateTimeOffset FromTime { get; private set; }

    public DateTimeOffset ToTime { get; private set; }

    public int Count { get; private set; }

    public string ObjectKey { get; private set; }
}

/// <summary>The <c>kernel_events</c> schema: the outbox, the inbox of every subscriber, and the archives.</summary>
[Schema("kernel_events")]
public sealed class EventsDbContext(DbContextOptions<EventsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    public DbSet<InboxEntry> Inbox => Set<InboxEntry>();

    public DbSet<EventArchive> Archives => Set<EventArchive>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxEvent>(outbox =>
        {
            outbox.ToTable("outbox");
            outbox.Property(e => e.Position).UseIdentityAlwaysColumn();
            outbox.HasIndex(e => e.Position).IsUnique();
            outbox.Property(e => e.Type).HasMaxLength(OutboxEvent.TypeMaxLength);
            outbox.Property(e => e.RecordType).HasMaxLength(OutboxEvent.RecordTypeMaxLength);
            outbox.Property(e => e.Tenant).HasMaxLength(OutboxEvent.TenantMaxLength);
            outbox.Property(e => e.CorrelationId).HasMaxLength(OutboxEvent.CorrelationMaxLength);
            outbox.Property(e => e.Data).HasColumnType("jsonb");

            // The dispatcher's poll: what it has not handed on yet, in order (the measurement of ADR-0060).
            outbox.HasIndex(e => e.Position, "ix_outbox_pending").HasFilter("fanned_at is null").HasDatabaseName("ix_outbox_pending");
            outbox.HasIndex(e => new { e.RecordType, e.RecordId, e.Position });
            outbox.HasIndex(e => new { e.Type, e.OccurredAt });
            outbox.HasIndex(e => e.CorrelationId);
            outbox.HasIndex(e => e.OccurredAt);
        });

        modelBuilder.Entity<InboxEntry>(inbox =>
        {
            inbox.ToTable("inbox");
            inbox.Property(i => i.Subscriber).HasMaxLength(InboxEntry.SubscriberMaxLength);
            inbox.Property(i => i.RecordType).HasMaxLength(OutboxEvent.RecordTypeMaxLength);
            inbox.Property(i => i.Status).HasMaxLength(10);
            inbox.Property(i => i.Suite).HasMaxLength(40);
            inbox.Property(i => i.LastError).HasMaxLength(InboxEntry.ErrorMaxLength);
            inbox.HasIndex(i => new { i.Subscriber, i.EventId }).IsUnique();
            inbox.HasIndex(i => i.EventId);

            // What is due, and what holds a later event of the same record back.
            inbox.HasIndex(i => new { i.NextAttemptAt, i.Position }, "ix_inbox_due").HasFilter("status = 'pending'").HasDatabaseName("ix_inbox_due");
            inbox.HasIndex(i => new { i.Subscriber, i.RecordType, i.RecordId, i.Position }, "ix_inbox_waiting").HasFilter("status = 'pending'").HasDatabaseName("ix_inbox_waiting");
            inbox.HasIndex(i => new { i.Status, i.DeadAt });
        });

        modelBuilder.Entity<EventArchive>(archive =>
        {
            archive.ToTable("archives");
            archive.Property(a => a.ObjectKey).HasMaxLength(EventArchive.KeyMaxLength);
            archive.HasIndex(a => a.ToTime);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class EventsDbContextFactory : IDesignTimeDbContextFactory<EventsDbContext>
{
    public EventsDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<EventsDbContext>());
}
