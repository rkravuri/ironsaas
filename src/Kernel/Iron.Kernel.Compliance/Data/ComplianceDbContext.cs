using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Compliance.Data;

/// <summary>The <c>kernel_compliance</c> schema: electronic signatures with their snapshots, and the reviews of the audit trail.</summary>
[Schema("kernel_compliance")]
public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Signature> Signatures => Set<Signature>();

    public DbSet<AuditTrailReview> AuditTrailReviews => Set<AuditTrailReview>();

    public DbSet<EvidencePack> EvidencePacks => Set<EvidencePack>();

    public DbSet<EvidenceDownloadLink> EvidenceDownloadLinks => Set<EvidenceDownloadLink>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvidencePack>(pack =>
        {
            pack.ToTable("evidence_packs");
            pack.Property(p => p.RequesterName).HasMaxLength(200);
            pack.Property(p => p.Status).HasMaxLength(20);
            pack.Property(p => p.BlobKey).HasMaxLength(200);
            pack.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength();
            pack.Property(p => p.ManifestSha256).HasMaxLength(64).IsFixedLength();
            pack.Property(p => p.ChainHash).HasMaxLength(64).IsFixedLength();
            pack.Property(p => p.Failure).HasMaxLength(EvidencePack.FailureMaxLength);
            pack.HasIndex(p => new { p.Status, p.CreatedAt });
        });

        modelBuilder.Entity<EvidenceDownloadLink>(link =>
        {
            link.ToTable("evidence_download_links");
            link.Property(l => l.TokenHash).HasMaxLength(64).IsFixedLength();
            link.HasIndex(l => l.TokenHash).IsUnique();
            link.HasIndex(l => l.PackId);
        });

        modelBuilder.Entity<Signature>(signature =>
        {
            signature.ToTable("signatures");
            signature.Property(s => s.RecordType).HasMaxLength(Signature.RecordTypeMaxLength);
            signature.Property(s => s.SnapshotHash).HasMaxLength(64).IsFixedLength();
            signature.Property(s => s.SignerName).HasMaxLength(200);
            signature.Property(s => s.SignerLoginId).HasMaxLength(320);
            signature.Property(s => s.Method).HasMaxLength(40);
            signature.Property(s => s.Meaning).HasMaxLength(20);
            signature.Property(s => s.Comment).HasMaxLength(Signature.CommentMaxLength);
            signature.HasIndex(s => new { s.RecordType, s.RecordId, s.CreatedAt });
            signature.HasIndex(s => s.SignerId);
        });

        modelBuilder.Entity<AuditTrailReview>(review =>
        {
            review.ToTable("audit_trail_reviews");
            review.Property(r => r.Outcome).HasMaxLength(20);
            review.Property(r => r.Notes).HasMaxLength(AuditTrailReview.NotesMaxLength);
            review.Property(r => r.SearchesJson).HasColumnName("searches").HasColumnType("jsonb");
            review.Property(r => r.ReviewerName).HasMaxLength(200);
            review.HasIndex(r => new { r.PeriodFrom, r.PeriodTo });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class ComplianceDbContextFactory : IDesignTimeDbContextFactory<ComplianceDbContext>
{
    public ComplianceDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<ComplianceDbContext>());
}
