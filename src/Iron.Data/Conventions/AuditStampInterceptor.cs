using Iron.Core.Principals;
using Iron.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Iron.Data.Conventions;

/// <summary>
/// Runs before every save. Fills created and updated columns from the injected <see cref="TimeProvider"/> and
/// <see cref="ICurrentPrincipal"/>, increments the version, turns a delete of an <see cref="ISoftDeletable"/> into a
/// marked row, and refuses changes to append-only rows. Registered per scope by AddIronDatabase.
/// </summary>
public sealed class AuditStampInterceptor : SaveChangesInterceptor
{
    private readonly TimeProvider _time;
    private readonly ICurrentPrincipal _principal;

    public AuditStampInterceptor(TimeProvider time, ICurrentPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(principal);
        _time = time;
        _principal = principal;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var actor = _principal.Id;

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AppendOnlyEntity)
            {
                StampAppendOnly(entry, now, actor);
                continue;
            }

            if (entry.Entity is ISoftDeletable && entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                Set(entry, nameof(ISoftDeletable.IsDeleted), true);
                Set(entry, nameof(ISoftDeletable.DeletedAt), now);
                Set(entry, nameof(ISoftDeletable.DeletedBy), actor);
            }

            if (entry.Entity is AuditedEntity)
            {
                StampAudited(entry, now, actor);
            }
        }
    }

    private static void StampAppendOnly(EntityEntry entry, DateTimeOffset now, Guid actor)
    {
        switch (entry.State)
        {
            case EntityState.Added:
                Set(entry, nameof(AppendOnlyEntity.CreatedAt), now);
                Set(entry, nameof(AppendOnlyEntity.CreatedBy), actor);
                break;
            case EntityState.Modified:
            case EntityState.Deleted:
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only; its rows are never changed or deleted.");
        }
    }

    private static void StampAudited(EntityEntry entry, DateTimeOffset now, Guid actor)
    {
        switch (entry.State)
        {
            case EntityState.Added:
                Set(entry, nameof(AuditedEntity.CreatedAt), now);
                Set(entry, nameof(AuditedEntity.CreatedBy), actor);
                Set(entry, nameof(AuditedEntity.UpdatedAt), now);
                Set(entry, nameof(AuditedEntity.UpdatedBy), actor);
                Set(entry, nameof(AuditedEntity.Version), 1);
                break;
            case EntityState.Modified:
                var version = entry.Property(nameof(AuditedEntity.Version));
                Set(entry, nameof(AuditedEntity.UpdatedAt), now);
                Set(entry, nameof(AuditedEntity.UpdatedBy), actor);
                version.CurrentValue = (int)(version.OriginalValue ?? 0) + 1;
                break;
        }
    }

    private static void Set(EntityEntry entry, string propertyName, object? value)
    {
        entry.Property(propertyName).CurrentValue = value;
    }
}
