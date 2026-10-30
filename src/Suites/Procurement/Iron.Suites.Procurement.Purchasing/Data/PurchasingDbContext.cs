using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Suites.Procurement.Purchasing.Data;

/// <summary>
/// The suite's installation in this tenant (TF-244, ADR-0059): when its schema was first migrated and seeded, by which
/// job and release, and when an update last did it again. One row per suite, written by the seed. It is the tenant's,
/// not a business record of a unit, and it stays when the suite is removed, so adding the suite again finds it.
/// </summary>
public sealed class SuiteInstallation : AuditedEntity, ITenantWide
{
    public const int VersionMaxLength = 64;

    private SuiteInstallation()
    {
        Suite = string.Empty;
        InstalledVersion = string.Empty;
        SeededVersion = string.Empty;
    }

    public SuiteInstallation(string suite, DateTimeOffset at, Guid? job, string version)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suite);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Suite = suite;
        InstalledAt = at;
        InstalledByJob = job;
        InstalledVersion = version;
        SeededAt = at;
        SeededByJob = job;
        SeededVersion = version;
    }

    /// <summary>The suite's short name: <c>procurement</c>.</summary>
    public string Suite { get; private set; }

    public DateTimeOffset InstalledAt { get; private set; }

    /// <summary>The fleet's job that installed it, when a job did (a laptop's <c>make seed</c> names none).</summary>
    public Guid? InstalledByJob { get; private set; }

    /// <summary>The release that installed it, or <c>development</c>.</summary>
    public string InstalledVersion { get; private set; }

    public DateTimeOffset SeededAt { get; private set; }

    public Guid? SeededByJob { get; private set; }

    public string SeededVersion { get; private set; }

    /// <summary>An update migrated and seeded the suite again: when, by which job, with which release.</summary>
    public void Seeded(DateTimeOffset at, Guid? job, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        SeededAt = at;
        SeededByJob = job;
        SeededVersion = version;
    }
}

/// <summary>The <c>procurement_purchasing</c> schema, made only in the units of tenants that bought the eProcurement Suite (TF-244).</summary>
[Schema("procurement_purchasing")]
public sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SuiteInstallation> Installations => Set<SuiteInstallation>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SuiteInstallation>(installation =>
        {
            installation.ToTable("installations");
            installation.Property(i => i.Suite).HasMaxLength(40);
            installation.HasIndex(i => i.Suite).IsUnique();
            installation.Property(i => i.InstalledVersion).HasMaxLength(SuiteInstallation.VersionMaxLength);
            installation.Property(i => i.SeededVersion).HasMaxLength(SuiteInstallation.VersionMaxLength);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class PurchasingDbContextFactory : IDesignTimeDbContextFactory<PurchasingDbContext>
{
    public PurchasingDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<PurchasingDbContext>());
}
