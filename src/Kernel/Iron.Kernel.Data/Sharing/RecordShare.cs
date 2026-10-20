using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Data.Sharing;

/// <summary>
/// One unit a shared business record is listed for (TF-222, ADR-0038), keyed by the record type's name and the record's
/// id, so one kernel table serves every module and a merge moves a unit's shares in one statement. Every module context
/// with business records maps this table too, read only, so a list's policy joins it in the same query.
/// </summary>
public sealed class RecordShare : AuditedEntity
{
    public const string Schema = "kernel_data";
    public const string Table = "record_shares";
    public const int RecordTypeMaxLength = 100;

    private RecordShare()
    {
        RecordType = string.Empty;
    }

    public RecordShare(string recordType, Guid recordId, Guid unitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        RecordType = recordType;
        RecordId = recordId;
        UnitId = unitId;
    }

    public string RecordType { get; private set; }

    public Guid RecordId { get; private set; }

    public Guid UnitId { get; private set; }

    /// <summary>A merge lists the record for the surviving unit instead (TF-223).</summary>
    public void MoveTo(Guid unitId) => UnitId = unitId;

    /// <summary>The mapping, the same in the data library's own context and, read only, in every module's.</summary>
    internal static void Map(ModelBuilder modelBuilder, bool owned)
    {
        modelBuilder.Entity<RecordShare>(share =>
        {
            share.ToTable(Table, Schema, table =>
            {
                if (!owned)
                {
                    table.ExcludeFromMigrations();
                }
            });
            share.Property(s => s.RecordType).HasMaxLength(RecordTypeMaxLength);
            share.HasIndex(s => new { s.RecordType, s.RecordId, s.UnitId }).IsUnique();
            share.HasIndex(s => s.UnitId);
        });
    }
}

/// <summary>
/// The data library's own schema, <c>kernel_data</c>: the units shared business records are listed for. It holds no
/// business data; a module's records stay in the module's schema.
/// </summary>
[Schema(RecordShare.Schema)]
public sealed class DataDbContext(DbContextOptions<DataDbContext> options) : ModuleDbContext(options)
{
    public DbSet<RecordShare> Shares => Set<RecordShare>();

    protected override void ConfigureModule(ModelBuilder modelBuilder) => RecordShare.Map(modelBuilder, owned: true);
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class DataDbContextFactory : IDesignTimeDbContextFactory<DataDbContext>
{
    public DataDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<DataDbContext>());
}
