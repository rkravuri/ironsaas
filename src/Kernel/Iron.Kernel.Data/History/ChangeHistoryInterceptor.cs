using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.History;

/// <summary>
/// Records field-level history (TF-062, ADR-0051). Before a save, it takes every tracked entity the save creates, changes
/// or deletes, with each tracked field's old and new value; after the save, it hands them to the
/// <see cref="IChangeRecorder"/>, which writes one audit event per record into the trail. Both happen in one transaction:
/// the caller's unit of work when one is open, otherwise one this interceptor opens for the save and commits after the
/// history is written. Runs after the stamp interceptor, so a record's version is the one the save writes.
/// </summary>
public sealed class ChangeHistoryInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    /// <summary>The columns every save stamps on its own: the history of a record is what people changed.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal)
    {
        nameof(Entity.Id),
        nameof(AuditedEntity.CreatedAt),
        nameof(AuditedEntity.CreatedBy),
        nameof(AuditedEntity.UpdatedAt),
        nameof(AuditedEntity.UpdatedBy),
        nameof(AuditedEntity.Version),
        nameof(ISoftDeletable.IsDeleted),
        nameof(ISoftDeletable.DeletedAt),
        nameof(ISoftDeletable.DeletedBy),
    };

    private readonly Dictionary<DbContext, Pending> _pending = new(ReferenceEqualityComparer.Instance);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        BeforeAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await BeforeAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AfterAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await AfterAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        FailedAsync(eventData.Context).GetAwaiter().GetResult();
        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await FailedAsync(eventData.Context).ConfigureAwait(false);
        await base.SaveChangesFailedAsync(eventData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The changes a save makes to tracked entities, as the history keeps them. Public for the model's own tests.</summary>
    public static IReadOnlyList<RecordChange> Collect(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var changes = new List<RecordChange>();
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity is ITrackedChanges))
        {
            var kind = entry.State switch
            {
                EntityState.Added => RecordChangeKinds.Created,
                EntityState.Modified => SoftDeleted(entry) ? RecordChangeKinds.Deleted : RecordChangeKinds.Changed,
                EntityState.Deleted => RecordChangeKinds.Deleted,
                _ => null,
            };
            if (kind is null)
            {
                continue;
            }

            var fields = new List<FieldChange>();
            foreach (var property in entry.Properties)
            {
                var metadata = property.Metadata;
                var info = metadata.PropertyInfo;
                if (info is null || Bookkeeping.Contains(metadata.Name) || IsLeftOut(info))
                {
                    continue;
                }

                var oldText = kind == RecordChangeKinds.Created ? null : Text(property.OriginalValue);
                var newText = kind == RecordChangeKinds.Deleted ? null : Text(property.CurrentValue);
                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    continue;
                }

                fields.Add(Treat(metadata.GetColumnName(), oldText, newText, info));
            }

            if (kind == RecordChangeKinds.Changed && fields.Count == 0)
            {
                continue; // only bookkeeping changed: nothing a person would read in the history
            }

            var version = entry.Entity is AuditedEntity ? (int?)entry.Property(nameof(AuditedEntity.Version)).CurrentValue : null;
            changes.Add(new RecordChange(kind, RecordTypeOf(entry), (Guid)entry.Property(nameof(Entity.Id)).CurrentValue!, version, fields));
        }

        return changes;
    }

    /// <summary>
    /// A tracked record's fields now, as its history writes them (ADR-0051): every field the history keeps, by its column,
    /// the masked ones masked and the secret ones out. With the history's later saves undone (the Audit module's
    /// <c>RecordStates</c>), it is the record as it stood at a time in the past (ADR-0052).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Fields(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            var metadata = property.Metadata;
            var info = metadata.PropertyInfo;
            if (info is null || Bookkeeping.Contains(metadata.Name) || IsLeftOut(info) || IsSecret(info))
            {
                continue;
            }

            var text = Text(property.CurrentValue);
            fields[metadata.GetColumnName()] = info.GetCustomAttribute<SensitiveAttribute>() is { } sensitive ? Mask(text, sensitive.Keep) : text;
        }

        return fields;
    }

    /// <summary>The record type in the history: the name <see cref="TrackedAsAttribute"/> gives, the class's <c>RecordType</c> constant, or its table's name.</summary>
    public static string RecordTypeOf(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordTypeOf(entry.Metadata);
    }

    /// <summary>The record type in the history of an entity type of a model.</summary>
    public static string RecordTypeOf(Microsoft.EntityFrameworkCore.Metadata.IEntityType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.ClrType.GetCustomAttribute<TrackedAsAttribute>() is { } named)
        {
            return named.RecordType;
        }

        var field = type.ClrType.GetField("RecordType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return field?.GetValue(null) as string ?? type.GetTableName() ?? type.ClrType.Name;
    }

    /// <summary>A value as the history keeps it: invariant text, times in UTC with microseconds, anything else as JSON.</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("D"),
        DateTimeOffset at => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateTime at => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Enum choice => choice.ToString(),
        IFormattable formattable when value.GetType().IsPrimitive || value is decimal => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };

    /// <summary>The last <paramref name="keep"/> characters of a sensitive value, the rest masked (TF-062 AC3).</summary>
    public static string? Mask(string? value, int keep)
    {
        if (value is null)
        {
            return null;
        }

        var shown = Math.Clamp(keep, 0, value.Length);
        return "****" + value[(value.Length - shown)..];
    }

    /// <summary>A secret, or an encrypted value (ADR-0054): its history says only that it changed, never the value or its ciphertext.</summary>
    private static bool IsSecret(PropertyInfo info) => info.IsDefined(typeof(SecretAttribute)) || info.PropertyType == typeof(Encryption.EncryptedValue);

    /// <summary>Bookkeeping, or a search hash, which changes with its encrypted value and says nothing of its own.</summary>
    private static bool IsLeftOut(PropertyInfo info) => info.IsDefined(typeof(NotTrackedAttribute)) || info.PropertyType == typeof(Encryption.SearchHash);

    private static FieldChange Treat(string field, string? oldText, string? newText, PropertyInfo info)
    {
        if (IsSecret(info))
        {
            return new FieldChange(field, null, null, FieldTreatments.Secret);
        }

        return info.GetCustomAttribute<SensitiveAttribute>() is { } sensitive
            ? new FieldChange(field, Mask(oldText, sensitive.Keep), Mask(newText, sensitive.Keep), FieldTreatments.Masked)
            : new FieldChange(field, oldText, newText);
    }

    private static bool SoftDeleted(EntityEntry entry) =>
        entry.Entity is ISoftDeletable
        && entry.Property(nameof(ISoftDeletable.IsDeleted)) is var deleted
        && deleted.CurrentValue is true
        && deleted.OriginalValue is not true;

    private async Task BeforeAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || services.GetService<IChangeRecorder>() is null)
        {
            return;
        }

        var changes = Collect(context);
        if (changes.Count == 0)
        {
            return;
        }

        // The history and the save commit together: in the caller's transaction, or in one opened for this save.
        ITransactionScope? scope = null;
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        if (!unitOfWork.IsActive && context.Database.CurrentTransaction is null)
        {
            scope = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        }

        _pending[context] = new Pending(changes, scope);
    }

    private async Task AfterAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.Remove(context, out var pending))
        {
            return;
        }

        try
        {
            await services.GetRequiredService<IChangeRecorder>().RecordAsync(pending.Changes, cancellationToken).ConfigureAwait(false);
            if (pending.Scope is not null)
            {
                await pending.Scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (pending.Scope is not null)
            {
                await pending.Scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task FailedAsync(DbContext? context)
    {
        if (context is not null && _pending.Remove(context, out var pending) && pending.Scope is not null)
        {
            await pending.Scope.DisposeAsync().ConfigureAwait(false); // without Commit: the save and its history roll back
        }
    }

    private sealed record Pending(IReadOnlyList<RecordChange> Changes, ITransactionScope? Scope);
}
