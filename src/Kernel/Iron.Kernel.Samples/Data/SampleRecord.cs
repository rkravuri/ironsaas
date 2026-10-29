using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Samples.Data;

/// <summary>
/// A business record with nothing of its own but a name, so the smoke and the browser tests meet the kernel's record
/// machinery in a running unit before any product brings records (ADR-0057): its policy, sharing, visibility tags and
/// field history, on a record page built from the record template.
/// </summary>
public sealed class SampleRecord : BusinessRecord, ITrackedChanges
{
    public const string RecordType = "sample_record";
    public const int NameMaxLength = 200;

    private SampleRecord()
    {
        Name = string.Empty;
    }

    public SampleRecord(Guid owningOrganisationId, string name)
        : base(owningOrganisationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public string Name { get; private set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }
}

/// <summary>The <c>kernel_samples</c> schema, made only in units that load the sample module.</summary>
[Schema("kernel_samples")]
public sealed class SamplesDbContext(DbContextOptions<SamplesDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SampleRecord> Records => Set<SampleRecord>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SampleRecord>(record =>
        {
            record.ToTable("records");
            record.Property(r => r.Name).HasMaxLength(SampleRecord.NameMaxLength);
            record.HasIndex(r => r.Name);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class SamplesDbContextFactory : IDesignTimeDbContextFactory<SamplesDbContext>
{
    public SamplesDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<SamplesDbContext>());
}
