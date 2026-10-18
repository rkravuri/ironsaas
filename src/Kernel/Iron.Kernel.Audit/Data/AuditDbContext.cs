using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Audit.Data;

/// <summary>The <c>audit</c> schema: the append-only event table (TF-060).</summary>
[Schema("kernel_audit")]
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : ModuleDbContext(options)
{
    public DbSet<AuditEventRecord> Events => Set<AuditEventRecord>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEventRecord>(record =>
        {
            record.ToTable("events");
            record.Property(e => e.EventType).HasMaxLength(AuditEventRecord.EventTypeMaxLength);
            record.Property(e => e.Outcome).HasMaxLength(20);
            record.Property(e => e.Source).HasMaxLength(10);
            record.Property(e => e.Target).HasMaxLength(AuditEventRecord.TargetMaxLength);
            record.Property(e => e.RecordType).HasMaxLength(100);
            record.Property(e => e.Reason).HasMaxLength(AuditEventRecord.ReasonMaxLength);
            record.Property(e => e.CorrelationId).HasMaxLength(CorrelationId.Length);
            record.Property(e => e.Details).HasColumnType("jsonb");
            record.HasIndex(e => e.CreatedAt);
            record.HasIndex(e => e.ActorId);
            record.HasIndex(e => e.EventType);
            record.HasIndex(e => e.CorrelationId);
            record.HasIndex(e => new { e.RecordType, e.RecordId });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<AuditDbContext>());
}
