using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.Tests.Sample;

public enum ProposalStatus
{
    Draft,
    Submitted,
}

/// <summary>A business record with a draft status: its creator may change it until it is submitted (TF-035).</summary>
public sealed class Proposal : BusinessRecord
{
    private Proposal()
    {
    }

    public Proposal(Guid owningOrganisationId, string title)
        : base(owningOrganisationId)
    {
        Title = title;
    }

    public string Title { get; private set; } = string.Empty;

    public ProposalStatus Status { get; private set; } = ProposalStatus.Draft;

    public void Retitle(string title) => Title = title;

    public void Submit() => Status = ProposalStatus.Submitted;
}

/// <summary>The sample module's permissions, as a module declares them in its Contracts.</summary>
public static class SamplePermissions
{
    public static readonly Permission WidgetsRead = new("sample.widgets.read", "View widgets", Risk.Low);

    public static readonly Permission WidgetsManage = new("sample.widgets.manage", "Change widgets", Risk.Medium);

    public static readonly Permission ProposalsRead = new("sample.proposals.read", "View proposals", Risk.Low);
}

/// <summary>The sample module's record policies, built from the kernel's blocks as a module writes them (ADR-0032).</summary>
public static class SamplePolicies
{
    public const string ProposalType = "sample_proposal";
    public const string WidgetType = "sample_widget";

    /// <summary>Proposals: seen in the reader's units; changed by their creator while a draft.</summary>
    public static IServiceCollection AddProposalPolicy(this IServiceCollection services) =>
        services.AddRecordPolicy(
            ProposalType,
            "Proposals",
            SamplePermissions.ProposalsRead,
            RecordRules.All(
                RecordRules.UnitVisibility<Proposal>(SamplePermissions.ProposalsRead),
                RecordRules.OwnerWhile<Proposal>(p => p.Status == ProposalStatus.Draft, RecordActions.Update)));

    /// <summary>Widgets: seen in the reader's units, changed and shared where the reader may manage them.</summary>
    public static IServiceCollection AddWidgetPolicy(this IServiceCollection services) =>
        services.AddRecordPolicy(
            WidgetType,
            "Widgets",
            SamplePermissions.WidgetsRead,
            RecordRules.UnitVisibility<Widget>(SamplePermissions.WidgetsRead, (RecordActions.Update, SamplePermissions.WidgetsManage), (RecordActions.Share, SamplePermissions.WidgetsManage)));
}

/// <summary>A context whose business records carry the markers of other entities, for the model check's refusal (ADR-0032).</summary>
[Schema("kernel_sample_marked")]
public sealed class ContextWithAMarkedRecord(DbContextOptions<ContextWithAMarkedRecord> options) : ModuleDbContext(options)
{
    public DbSet<MarkedRecord> MarkedRecords => Set<MarkedRecord>();

    public DbSet<OwnRuleRecord> OwnRuleRecords => Set<OwnRuleRecord>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}

public sealed class MarkedRecord : BusinessRecord, ITenantWide
{
    private MarkedRecord()
    {
    }

    public MarkedRecord(Guid owningOrganisationId)
        : base(owningOrganisationId)
    {
    }
}

public sealed class OwnRuleRecord : BusinessRecord, IOwnAccessRule
{
    private OwnRuleRecord()
    {
    }

    public OwnRuleRecord(Guid owningOrganisationId)
        : base(owningOrganisationId)
    {
    }
}
