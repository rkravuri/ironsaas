using Iron.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Files.Data;

/// <summary>The <c>files</c> schema: file metadata (TF-160).</summary>
[Schema("files")]
public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options) : ModuleDbContext(options)
{
    public DbSet<StoredFile> Files => Set<StoredFile>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(file =>
        {
            file.Property(f => f.Name).HasMaxLength(StoredFile.NameMaxLength);
            file.Property(f => f.ContentType).HasMaxLength(StoredFile.ContentTypeMaxLength);
            file.Property(f => f.Sha256).HasMaxLength(64);
            file.Property(f => f.StorageKey).HasMaxLength(StoredFile.StorageKeyMaxLength);
            file.HasIndex(f => f.StorageKey).IsUnique();
            file.Property(f => f.RecordType).HasMaxLength(100);
            file.HasIndex(f => new { f.RecordType, f.RecordId });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<FilesDbContext>());
}
