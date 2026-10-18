using Iron.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Tenancy.Data;

/// <summary>The <c>tenancy</c> schema: the tenant registry and its hostnames (TF-001).</summary>
[Schema("tenancy")]
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantHostname> Hostnames => Set<TenantHostname>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.Property(t => t.Slug).HasMaxLength(63);
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.DisplayName).HasMaxLength(Tenant.DisplayNameMaxLength);
            tenant.Property(t => t.Region).HasMaxLength(32);
            tenant.Property(t => t.Plan).HasMaxLength(64);
            tenant.Property(t => t.SizeClass).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.HasIndex(t => t.Status);
            tenant.Property(t => t.AdministratorLoginId).HasMaxLength(254);
            tenant.Property(t => t.AdministratorDisplayName).HasMaxLength(200);
            tenant.Property(t => t.DatabaseName).HasMaxLength(63);
            tenant.Property(t => t.StorageContainer).HasMaxLength(63);
        });

        modelBuilder.Entity<TenantHostname>(hostname =>
        {
            hostname.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId).OnDelete(DeleteBehavior.Cascade);
            hostname.Property(h => h.Hostname).HasMaxLength(TenantHostname.MaxLength);
            hostname.HasIndex(h => h.Hostname).IsUnique();
            hostname.HasIndex(h => h.TenantId);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<TenancyDbContext>());
}
