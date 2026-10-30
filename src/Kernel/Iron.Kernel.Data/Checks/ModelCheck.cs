using System.Reflection;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Iron.Kernel.Data.Checks;

/// <summary>
/// Checks an Entity Framework model before any database exists: every mapped type derives from <see cref="Entity"/>,
/// and in a business module (a product) every entity is a <see cref="BusinessRecord"/> unless it is marked
/// <see cref="ITenantWide"/> on purpose (TF-220: the build fails when a business entity lacks the owning organisation).
/// In every module, a business record carries neither ITenantWide nor IOwnAccessRule, which would let it page without its policy (ADR-0032),
/// and records its field-level history: it is marked <see cref="ITrackedChanges"/> (TF-062 AC2, ADR-0051); and its table
/// carries its visibility tags in a <c>text[]</c> column named <c>visibility_tags</c> (ADR-0057). Build the model with
/// <see cref="Contexts.DesignTime"/>'s options, as a host does, so its names are the table's.
/// </summary>
public static class ModelCheck
{
    private const string VisibilityTagsColumn = "visibility_tags";

    public static IReadOnlyList<string> Check(IModel model, string contextName, bool isBusinessModule)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);

        var violations = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            // An owned type is part of its owner. The kernel's record shares belong to the data library's own context;
            // every context with business records maps them read only, to join them in its lists (ADR-0038).
            if (entityType.IsOwned() || entityType.ClrType == typeof(RecordShare))
            {
                continue;
            }

            var clrType = entityType.ClrType;
            if (!typeof(Entity).IsAssignableFrom(clrType))
            {
                violations.Add($"{contextName}: {clrType.Name} does not derive from Entity, AuditedEntity, AppendOnlyEntity or BusinessRecord.");
                continue;
            }

            violations.AddRange(EncryptedFieldViolations(contextName, entityType));

            if (typeof(BusinessRecord).IsAssignableFrom(clrType) && typeof(IOwnAccessRule).IsAssignableFrom(clrType))
            {
                var marker = typeof(ITenantWide).IsAssignableFrom(clrType) ? nameof(ITenantWide) : nameof(IOwnAccessRule);
                violations.Add(
                    $"{contextName}: {clrType.Name} is a BusinessRecord marked {marker}. A business record is read and paged through its record policy; remove {marker} (ADR-0032).");
                continue;
            }

            if (typeof(BusinessRecord).IsAssignableFrom(clrType) && !typeof(ITrackedChanges).IsAssignableFrom(clrType))
            {
                violations.Add(
                    $"{contextName}: {clrType.Name} is a business record without its change history. Mark it ITrackedChanges, with Sensitive or Secret on the fields that need it (TF-062).");
                continue;
            }

            if (typeof(BusinessRecord).IsAssignableFrom(clrType) && !HasVisibilityTagsColumn(entityType))
            {
                violations.Add(
                    $"{contextName}: {clrType.Name} is a business record whose table has no {VisibilityTagsColumn} column. Leave BusinessRecord.VisibilityTags as the conventions map it, a text[] column named {VisibilityTagsColumn} of the record's own table (TF-241, ADR-0057).");
                continue;
            }

            if (isBusinessModule
                && !typeof(BusinessRecord).IsAssignableFrom(clrType)
                && !typeof(ITenantWide).IsAssignableFrom(clrType))
            {
                violations.Add(
                    $"{contextName}: {clrType.Name} is a business entity without an owning organisation. Derive from BusinessRecord, or mark it ITenantWide on purpose (TF-220).");
            }
        }

        return violations;
    }

    /// <summary>
    /// The conventions map a business record's tags to a <c>text[]</c> column named <c>visibility_tags</c> of its own table
    /// (ADR-0057), and a module could still rename it, convert it or map the record to no table: the policy's tag filter
    /// cannot query a converted column, and the history names a field by its column, so a renamed one would show the
    /// tags under another name.
    /// </summary>
    private static bool HasVisibilityTagsColumn(IEntityType entityType)
    {
        var property = entityType.FindProperty(nameof(BusinessRecord.VisibilityTags));
        if (property is null || entityType.GetTableName() is not { } tableName)
        {
            return false;
        }

        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        return property.GetColumnName(table) == VisibilityTagsColumn
            && property.GetRelationalTypeMapping().StoreType == "text[]";
    }

    /// <summary>
    /// A searchable encrypted field keeps its hash beside it, and a hash belongs to a searchable encrypted field (ADR-0054):
    /// a hash of nothing, or a searchable field without its hash, would make a search that silently finds nothing.
    /// </summary>
    private static IEnumerable<string> EncryptedFieldViolations(string contextName, IEntityType entityType)
    {
        var clrType = entityType.ClrType;
        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.IsDefined(typeof(SearchableAttribute)))
            {
                var hashName = SearchableAttribute.HashPropertyOf(property.Name);
                if (property.PropertyType != typeof(EncryptedValue))
                {
                    yield return $"{contextName}: {clrType.Name}.{property.Name} is marked Searchable but is not an EncryptedValue (ADR-0054).";
                }
                else if (clrType.GetProperty(hashName)?.PropertyType != typeof(SearchHash))
                {
                    yield return $"{contextName}: {clrType.Name}.{property.Name} is searchable without its hash. Add a SearchHash property named {hashName} (ADR-0054).";
                }
            }
            else if (property.PropertyType == typeof(SearchHash))
            {
                var source = property.Name.EndsWith("Hash", StringComparison.Ordinal) ? clrType.GetProperty(property.Name[..^4]) : null;
                if (source is null || source.PropertyType != typeof(EncryptedValue) || !source.IsDefined(typeof(SearchableAttribute)))
                {
                    yield return $"{contextName}: {clrType.Name}.{property.Name} is a search hash of no searchable encrypted field. Name it after an EncryptedValue property marked Searchable, with Hash after it (ADR-0054).";
                }
            }
        }
    }
}
