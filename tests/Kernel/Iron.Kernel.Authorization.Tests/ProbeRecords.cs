using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// A module's business record as a product would have one, for the tests of the working unit, the consolidated view and
/// merging units against the real tree (ADR-0038). Its table is created from the model; it has no migrations.
/// </summary>
public sealed class Crate : BusinessRecord
{
    private Crate()
    {
    }

    public Crate(Guid owningUnitId, string label)
        : base(owningUnitId)
    {
        Label = label;
    }

    public string Label { get; private set; } = string.Empty;
}

[Schema("kernel_probe")]
public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Crate> Crates => Set<Crate>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}

/// <summary>The probe's checklist for a unit, as a product supplies one (TF-223): a unit is ready once it holds a crate.</summary>
public sealed class CrateReadiness(ProbeDbContext context) : IUnitReadiness
{
    public const string FirstCrate = "probe.first_crate";

    public async Task<IReadOnlyList<ReadinessItem>> CheckAsync(OrganisationUnitInfo unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var done = await context.Crates.AnyAsync(c => c.OwningOrganisationId == unit.Id, cancellationToken);
        return [new ReadinessItem("probe", FirstCrate, "A first crate in the unit", done)];
    }
}

public static class ProbeRecords
{
    public const string CrateType = "probe_crate";

    public static readonly Permission CratesRead = new("probe.crates.read", "View crates", Risk.Low);

    public static readonly Permission CratesManage = new("probe.crates.manage", "Change and share crates", Risk.Medium);

    /// <summary>The probe module's context, its crates' policy and its records for merging, as a module's AddServices would.</summary>
    public static void Add(IServiceCollection services)
    {
        services.AddIronDbContext<ProbeDbContext>(options => options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddRecordPolicy(CrateType, CratesRead, RecordRules.UnitVisibility<Crate>(CratesRead, (RecordActions.Update, CratesManage), (RecordActions.Share, CratesManage)));
        services.AddUnitRecordOwners<ProbeDbContext>("probe");
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitReadiness, CrateReadiness>());
    }

    /// <summary>The crates table, made from the model once the modules' own migrations have run.</summary>
    public static async Task CreateTablesAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(TestContext.Current.CancellationToken);
    }
}
