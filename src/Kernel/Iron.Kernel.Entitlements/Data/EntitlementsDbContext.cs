using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Entitlements.Data;

/// <summary>The <c>kernel_entitlements</c> schema: the last known answer of the store and the seats of per-person modules.</summary>
[Schema("kernel_entitlements")]
public sealed class EntitlementsDbContext(DbContextOptions<EntitlementsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<LastKnownEntitlements> LastKnown => Set<LastKnownEntitlements>();

    public DbSet<SeatAssignment> Seats => Set<SeatAssignment>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LastKnownEntitlements>(row =>
        {
            row.ToTable("last_known");
            row.Property(r => r.Key).HasMaxLength(20);
            row.HasIndex(r => r.Key).IsUnique();
            row.Property(r => r.Document).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SeatAssignment>(seat =>
        {
            seat.ToTable("seats");
            seat.Property(s => s.ModuleCode).HasMaxLength(64);
            seat.HasIndex(s => new { s.ModuleCode, s.PrincipalId }).IsUnique();
            seat.HasIndex(s => s.PrincipalId);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class EntitlementsDbContextFactory : IDesignTimeDbContextFactory<EntitlementsDbContext>
{
    public EntitlementsDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<EntitlementsDbContext>());
}
