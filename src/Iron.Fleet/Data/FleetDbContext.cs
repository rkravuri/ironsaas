using Iron.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Fleet.Data;

/// <summary>The <c>fleet</c> schema: provisioning jobs (TF-002). Steps join in T14.</summary>
[Schema("fleet")]
public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ProvisioningJob> ProvisioningJobs => Set<ProvisioningJob>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProvisioningJob>(job =>
        {
            job.Property(j => j.TenantSlug).HasMaxLength(63);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.CurrentStep).HasMaxLength(50);
            job.Property(j => j.Error).HasMaxLength(2000);
            job.HasIndex(j => new { j.TenantSlug, j.RequestedAt });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class FleetDbContextFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<FleetDbContext>());
}
