using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Audit.Data;

/// <summary>The <c>audit</c> schema: the append-only event table (TF-060) and its chain (TF-061).</summary>
[Schema("kernel_audit")]
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : ModuleDbContext(options)
{
    /// <summary>A SHA-256 in lowercase hexadecimal.</summary>
    public const int HashLength = 64;

    public DbSet<AuditEventRecord> Events => Set<AuditEventRecord>();

    public DbSet<AuditSeal> Seals => Set<AuditSeal>();

    public DbSet<AuditArchiveBatch> ArchiveBatches => Set<AuditArchiveBatch>();

    public DbSet<AuditPrune> Prunes => Set<AuditPrune>();

    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();

    public DbSet<RetentionChoice> RetentionChoices => Set<RetentionChoice>();

    public DbSet<AuditExport> Exports => Set<AuditExport>();

    public DbSet<AuditChainRestore> ChainRestores => Set<AuditChainRestore>();

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
            record.Property(e => e.Changes).HasColumnType("jsonb");
            record.Property(e => e.Severity).HasMaxLength(AuditEventRecord.SeverityMaxLength).HasDefaultValue("normal");
            record.HasIndex(e => e.CreatedAt);
            record.HasIndex(e => e.ActorId);
            record.HasIndex(e => e.ImpersonatorId);
            record.HasIndex(e => e.OnBehalfOfId);
            record.HasIndex(e => e.EventType);
            record.HasIndex(e => e.CorrelationId);
            record.HasIndex(e => new { e.RecordType, e.RecordId });

            // The chain (TF-061, ADR-0050): set once by kernel_audit.seal_event, which the migration defines.
            record.Property(e => e.PreviousHash).HasMaxLength(HashLength).IsFixedLength();
            record.Property(e => e.Hash).HasMaxLength(HashLength).IsFixedLength();
            record.HasIndex(e => e.ChainPosition).IsUnique();
            record.HasIndex(e => e.SealedAt);
        });

        modelBuilder.Entity<AuditSeal>(seal =>
        {
            seal.ToTable("seals");
            seal.Property(s => s.LastHash).HasMaxLength(HashLength).IsFixedLength();
            seal.HasIndex(s => s.LastPosition).IsUnique();
        });

        modelBuilder.Entity<AuditArchiveBatch>(batch =>
        {
            batch.ToTable("archive_batches");
            batch.Property(b => b.BlobKey).HasMaxLength(AuditArchiveBatch.KeyMaxLength);
            batch.Property(b => b.ContentSha256).HasMaxLength(HashLength).IsFixedLength();
            batch.Property(b => b.LastHash).HasMaxLength(HashLength).IsFixedLength();
            batch.HasIndex(b => b.LastPosition).IsUnique();
            batch.HasIndex(b => new { b.FirstAt, b.LastAt });
        });

        modelBuilder.Entity<AuditPrune>(prune =>
        {
            prune.ToTable("prunes");
            prune.Property(p => p.HashAtPosition).HasMaxLength(HashLength).IsFixedLength();
            prune.HasIndex(p => p.UpToPosition).IsUnique();
        });

        modelBuilder.Entity<LegalHold>(hold =>
        {
            hold.Property(h => h.Scope).HasMaxLength(10);
            hold.Property(h => h.RecordType).HasMaxLength(LegalHold.RecordTypeMaxLength);
            hold.Property(h => h.Reason).HasMaxLength(LegalHold.ReasonMaxLength);
            hold.Property(h => h.ReleaseReason).HasMaxLength(LegalHold.ReasonMaxLength);
            hold.Ignore(h => h.Active);
            hold.HasIndex(h => new { h.RecordType, h.RecordId });
        });

        modelBuilder.Entity<RetentionChoice>(choice =>
        {
            choice.Property(c => c.ClassCode).HasMaxLength(50);
            choice.HasIndex(c => c.ClassCode).IsUnique();
        });

        modelBuilder.Entity<AuditChainRestore>(restore =>
        {
            restore.ToTable("chain_restores");
            restore.Property(r => r.PreviousHash).HasMaxLength(HashLength).IsFixedLength();
            restore.Property(r => r.Ticket).HasMaxLength(100);
            restore.Property(r => r.Reason).HasMaxLength(AuditEventRecord.ReasonMaxLength);
            restore.Property(r => r.AcceptedBy).HasMaxLength(200);
            restore.HasIndex(r => r.Segment).IsUnique();
        });

        modelBuilder.Entity<AuditExport>(export =>
        {
            export.ToTable("exports");
            export.Property(e => e.Filters).HasColumnType("jsonb");
            export.Property(e => e.BlobKey).HasMaxLength(AuditArchiveBatch.KeyMaxLength);
            export.Property(e => e.Sha256).HasMaxLength(HashLength).IsFixedLength();
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<AuditDbContext>());
}
