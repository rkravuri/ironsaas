using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Flags.Data;

/// <summary>The <c>kernel_flags</c> schema: the last flag rules the store gave this unit.</summary>
[Schema("kernel_flags")]
public sealed class FlagsDbContext(DbContextOptions<FlagsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<LastKnownFlagRules> LastKnown => Set<LastKnownFlagRules>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LastKnownFlagRules>(row =>
        {
            row.ToTable("last_known");
            row.Property(r => r.Key).HasMaxLength(20);
            row.HasIndex(r => r.Key).IsUnique();
            row.Property(r => r.PlatformDocument).HasColumnType("jsonb");
            row.Property(r => r.TenantDocument).HasColumnType("jsonb");
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class FlagsDbContextFactory : IDesignTimeDbContextFactory<FlagsDbContext>
{
    public FlagsDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<FlagsDbContext>());
}
