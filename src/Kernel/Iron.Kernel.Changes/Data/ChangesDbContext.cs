using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Changes.Data;

/// <summary>The <c>kernel_changes</c> schema: the changes of sensitive configuration that wait, or waited, for a second administrator.</summary>
[Schema("kernel_changes")]
public sealed class ChangesDbContext(DbContextOptions<ChangesDbContext> options) : ModuleDbContext(options)
{
    public DbSet<PendingChange> Changes => Set<PendingChange>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingChange>(change =>
        {
            change.ToTable("pending_changes");
            change.Property(c => c.Area).HasMaxLength(40);
            change.Property(c => c.Kind).HasMaxLength(60);
            change.Property(c => c.Subject).HasMaxLength(200);
            change.Property(c => c.Title).HasMaxLength(PendingChange.TitleMaxLength);
            change.Property(c => c.BeforeJson).HasColumnName("before").HasColumnType("jsonb");
            change.Property(c => c.AfterJson).HasColumnName("after").HasColumnType("jsonb");
            change.Property(c => c.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            change.Property(c => c.Permission).HasMaxLength(100);
            change.Property(c => c.Reason).HasMaxLength(PendingChange.ReasonMaxLength);
            change.Property(c => c.RequesterName).HasMaxLength(200);
            change.Property(c => c.Status).HasMaxLength(20);
            change.Property(c => c.RestartNote).HasMaxLength(2000);
            change.Property(c => c.DeciderName).HasMaxLength(200);
            change.Property(c => c.DecisionReason).HasMaxLength(PendingChange.ReasonMaxLength);
            change.Ignore(c => c.Scope);
            change.Ignore(c => c.Before);
            change.Ignore(c => c.After);
            change.Ignore(c => c.Payload);

            // One pending change per subject (ADR-0052): a second save of the same thing waits for the first to end.
            change.HasIndex(c => c.Subject).IsUnique().HasFilter($"status = '{PendingChangeStates.Pending}'");
            change.HasIndex(c => new { c.Status, c.ExpiresAt });
            change.HasIndex(c => new { c.RequestedById, c.CreatedAt });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class ChangesDbContextFactory : IDesignTimeDbContextFactory<ChangesDbContext>
{
    public ChangesDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<ChangesDbContext>());
}
