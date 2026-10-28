using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Monitoring.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Monitoring.Data;

/// <summary>
/// A security alert (TF-079, ADR-0056): the rule that raised it, the group (the principal, the address, the target, the
/// record), what was counted over which window, and the link to the audit search that shows the events. In a unit every
/// alert is its own; the control plane keeps its own and a copy of each unit's, by the unit's id. Acknowledged and
/// resolved by someone, each audited. Its revision counts its changes, so a copy and the operator's alerting see each one.
/// </summary>
public sealed class Alert : AuditedEntity, ITenantWide
{
    public const int TenantMaxLength = 63;
    public const int RuleMaxLength = 60;
    public const int TitleMaxLength = 200;
    public const int GroupMaxLength = 500;
    public const int TextMaxLength = 1000;
    public const int LinkMaxLength = 2000;
    public const int NameMaxLength = 200;

    /// <summary>Where the alert was raised: here, or in a unit that sent its copy.</summary>
    public const string Local = "local";
    public const string Copy = "copy";

    private Alert()
    {
        Tenant = string.Empty;
        Origin = Local;
        Rule = string.Empty;
        Title = string.Empty;
        Kind = AlertKinds.Threshold;
        Severity = AlertSeverity.Normal;
        State = AlertStates.Open;
        GroupKind = string.Empty;
        GroupValue = string.Empty;
    }

    /// <summary>An alert raised here.</summary>
    public Alert(string tenant, string rule, string title, string kind, string severity, string groupKind, string groupValue, long count, int threshold, int windowMinutes, DateTimeOffset? firstEventAt, DateTimeOffset? lastEventAt, DateTimeOffset raisedAt, string? reason, string? link, string? search, string? runbook)
        : this()
    {
        SourceId = Id;
        Tenant = tenant;
        Rule = rule;
        Title = Trim(title, TitleMaxLength)!;
        Kind = kind;
        Severity = severity;
        GroupKind = groupKind;
        GroupValue = Trim(groupValue, GroupMaxLength)!;
        Count = count;
        Threshold = threshold;
        WindowMinutes = windowMinutes;
        FirstEventAt = firstEventAt;
        LastEventAt = lastEventAt;
        RaisedAt = raisedAt;
        Reason = Trim(reason, TextMaxLength);
        Link = Trim(link, LinkMaxLength);
        Search = Trim(search, LinkMaxLength);
        Runbook = runbook;
        Revision = 1;
    }

    /// <summary>A unit's alert, as its first copy arrives in the control plane.</summary>
    public static Alert CopyOf(string tenant, AlertCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var alert = new Alert { SourceId = copy.Id, Tenant = tenant, Origin = Copy };
        alert.Take(copy);
        return alert;
    }

    public string Tenant { get; private set; }

    public string Origin { get; private set; }

    /// <summary>The alert's id where it was raised: its own here, the unit's for a copy.</summary>
    public Guid SourceId { get; private set; }

    public string Rule { get; private set; }

    public string Title { get; private set; }

    public string Kind { get; private set; }

    public string Severity { get; private set; }

    public string State { get; private set; }

    /// <summary><c>principal</c>, <c>address</c>, <c>target</c>, <c>record</c>, <c>all</c>, or <c>rule</c> for a failing rule.</summary>
    public string GroupKind { get; private set; }

    public string GroupValue { get; private set; }

    /// <summary>What was counted: the events, or what their detail adds up to.</summary>
    public long Count { get; private set; }

    public int Threshold { get; private set; }

    public int WindowMinutes { get; private set; }

    public DateTimeOffset? FirstEventAt { get; private set; }

    public DateTimeOffset? LastEventAt { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    /// <summary>Why a rule is failing, or the summary a copy carries.</summary>
    public string? Reason { get; private set; }

    /// <summary>The shell's audit search with the events behind the alert (a path in the tenant's shell).</summary>
    public string? Link { get; private set; }

    /// <summary>The same search on the API, for a client or a test.</summary>
    public string? Search { get; private set; }

    /// <summary>The runbook's section for the rule, a path in the repository at the release's version.</summary>
    public string? Runbook { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public Guid? AcknowledgedById { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedById { get; private set; }

    public string? Note { get; private set; }

    public int Revision { get; private set; }

    /// <summary>The revision the control plane holds (a unit's own alert).</summary>
    public int CopiedRevision { get; private set; }

    /// <summary>The revision the operator's alerting was last told (the control plane).</summary>
    public int SentRevision { get; private set; }

    /// <summary>The burst goes on within the rule's cool-down: the same alert counts it, so one burst is one alert.</summary>
    public void Absorb(long measure, DateTimeOffset lastEventAt)
    {
        Count += measure;
        LastEventAt = lastEventAt > LastEventAt ? lastEventAt : LastEventAt;
        Revision++;
    }

    public void Acknowledge(Guid by, DateTimeOffset at, string? note)
    {
        State = AlertStates.Acknowledged;
        AcknowledgedById = by;
        AcknowledgedAt = at;
        Note = Trim(note, TextMaxLength) ?? Note;
        Revision++;
    }

    public void Resolve(Guid by, DateTimeOffset at, string note)
    {
        State = AlertStates.Resolved;
        ResolvedById = by;
        ResolvedAt = at;
        Note = Trim(note, TextMaxLength);
        Revision++;
    }

    public void Copied(int revision) => CopiedRevision = Math.Max(CopiedRevision, revision);

    public void Sent(int revision) => SentRevision = Math.Max(SentRevision, revision);

    /// <summary>A newer copy from the unit.</summary>
    public void Take(AlertCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        Rule = copy.Rule;
        Title = Trim(copy.Title, TitleMaxLength)!;
        Kind = copy.Kind;
        Severity = copy.Severity;
        State = copy.State;
        GroupKind = copy.GroupKind;
        GroupValue = Trim(copy.GroupValue, GroupMaxLength)!;
        Count = copy.Count;
        Threshold = copy.Threshold;
        WindowMinutes = copy.WindowMinutes;
        FirstEventAt = copy.FirstEventAt;
        LastEventAt = copy.LastEventAt;
        RaisedAt = copy.RaisedAt;
        AcknowledgedAt = copy.AcknowledgedAt;
        ResolvedAt = copy.ResolvedAt;
        Reason = Trim(copy.Reason, TextMaxLength);
        Link = Trim(copy.Link, LinkMaxLength);
        Runbook = copy.Runbook;
        Revision = copy.Revision;
    }

    /// <summary>The copy this alert becomes in the control plane.</summary>
    public AlertCopy ToCopy(string groupValue) =>
        new(Id, Revision, Rule, Title, Kind, Severity, State, GroupKind, groupValue, Count, Threshold, WindowMinutes, FirstEventAt, LastEventAt, RaisedAt, AcknowledgedAt, ResolvedAt, Reason, Link, Runbook);

    private static string? Trim(string? value, int maximum) =>
        value is null ? null : value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}

/// <summary>A tenant's stricter values for a rule (ADR-0056): each null keeps the operator's.</summary>
public sealed class AlertRuleValue : AuditedEntity, ITenantWide
{
    private AlertRuleValue()
    {
        Rule = string.Empty;
    }

    public AlertRuleValue(string rule)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        Rule = rule;
    }

    public string Rule { get; private set; }

    public int? Threshold { get; private set; }

    public int? WindowMinutes { get; private set; }

    public long? Above { get; private set; }

    public void Set(int? threshold, int? windowMinutes, long? above)
    {
        Threshold = threshold;
        WindowMinutes = windowMinutes;
        Above = above;
    }
}

/// <summary>
/// How a rule last ran (TF-079 AC4): running or failing, with the reason, when it last ran and up to when it counted, so
/// a broken rule is a state someone sees and a pass that resumes after a gap knows what it missed.
/// </summary>
public sealed class AlertRuleState : AuditedEntity, ITenantWide
{
    public const string Running = "running";
    public const string Failing = "failing";

    private AlertRuleState()
    {
        Rule = string.Empty;
        State = Running;
    }

    public AlertRuleState(string rule)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        Rule = rule;
    }

    public string Rule { get; private set; }

    public string State { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public DateTimeOffset? FailingSince { get; private set; }

    /// <summary>The end of the window the last pass that ran counted to.</summary>
    public DateTimeOffset? CountedTo { get; private set; }

    public void Ran(DateTimeOffset at, DateTimeOffset countedTo)
    {
        State = Running;
        Reason = null;
        FailingSince = null;
        LastRunAt = at;
        CountedTo = countedTo;
    }

    public void Fail(DateTimeOffset at, string reason)
    {
        if (State != Failing)
        {
            FailingSince = at;
        }

        State = Failing;
        Reason = reason.Length <= Alert.TextMaxLength ? reason : reason[..(Alert.TextMaxLength - 1)] + "…";
        LastRunAt = at;
    }
}

/// <summary>The <c>kernel_monitoring</c> schema: the alerts, the tenant's stricter rule values, and how each rule last ran.</summary>
[Schema("kernel_monitoring")]
public sealed class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<AlertRuleValue> RuleValues => Set<AlertRuleValue>();

    public DbSet<AlertRuleState> RuleStates => Set<AlertRuleState>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alert>(alert =>
        {
            alert.ToTable("alerts");
            alert.Property(a => a.Tenant).HasMaxLength(Alert.TenantMaxLength);
            alert.Property(a => a.Origin).HasMaxLength(10);
            alert.Property(a => a.Rule).HasMaxLength(Alert.RuleMaxLength);
            alert.Property(a => a.Title).HasMaxLength(Alert.TitleMaxLength);
            alert.Property(a => a.Kind).HasMaxLength(20);
            alert.Property(a => a.Severity).HasMaxLength(20);
            alert.Property(a => a.State).HasMaxLength(20);
            alert.Property(a => a.GroupKind).HasMaxLength(20);
            alert.Property(a => a.GroupValue).HasMaxLength(Alert.GroupMaxLength);
            alert.Property(a => a.Reason).HasMaxLength(Alert.TextMaxLength);
            alert.Property(a => a.Link).HasMaxLength(Alert.LinkMaxLength);
            alert.Property(a => a.Search).HasMaxLength(Alert.LinkMaxLength);
            alert.Property(a => a.Runbook).HasMaxLength(Alert.NameMaxLength);
            alert.Property(a => a.Note).HasMaxLength(Alert.TextMaxLength);
            alert.HasIndex(a => new { a.Tenant, a.SourceId }).IsUnique();
            alert.HasIndex(a => new { a.Rule, a.GroupValue, a.RaisedAt });
            alert.HasIndex(a => new { a.State, a.RaisedAt });
        });

        modelBuilder.Entity<AlertRuleValue>(value =>
        {
            value.ToTable("rule_values");
            value.Property(v => v.Rule).HasMaxLength(Alert.RuleMaxLength);
            value.HasIndex(v => v.Rule).IsUnique();
        });

        modelBuilder.Entity<AlertRuleState>(state =>
        {
            state.ToTable("rule_states");
            state.Property(s => s.Rule).HasMaxLength(Alert.RuleMaxLength);
            state.Property(s => s.State).HasMaxLength(20);
            state.Property(s => s.Reason).HasMaxLength(Alert.TextMaxLength);
            state.HasIndex(s => s.Rule).IsUnique();
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class MonitoringDbContextFactory : IDesignTimeDbContextFactory<MonitoringDbContext>
{
    public MonitoringDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<MonitoringDbContext>());
}
