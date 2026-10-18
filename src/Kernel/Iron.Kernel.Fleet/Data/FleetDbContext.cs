using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Fleet.Data;

/// <summary>The <c>fleet</c> schema: provisioning jobs and their steps (TF-002).</summary>
[Schema("fleet")]
public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ProvisioningJob> ProvisioningJobs => Set<ProvisioningJob>();

    public DbSet<ProvisioningStep> ProvisioningSteps => Set<ProvisioningStep>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProvisioningJob>(job =>
        {
            job.Property(j => j.TenantSlug).HasMaxLength(63);
            job.Property(j => j.Kind).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.CurrentStep).HasMaxLength(50);
            job.Property(j => j.Error).HasMaxLength(2000);
            job.Property(j => j.Output).HasMaxLength(2000);
            job.HasIndex(j => new { j.TenantSlug, j.RequestedAt });
        });

        modelBuilder.Entity<ProvisioningStep>(step =>
        {
            step.HasOne<ProvisioningJob>().WithMany().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
            step.Property(s => s.Name).HasMaxLength(ProvisioningStep.NameMaxLength);
            step.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            step.Property(s => s.Output).HasMaxLength(2000);
            step.Property(s => s.Error).HasMaxLength(2000);
            step.HasIndex(s => new { s.JobId, s.Order }).IsUnique();
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class FleetDbContextFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<FleetDbContext>());
}
