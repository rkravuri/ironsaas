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
/// and records its field-level history: it is marked <see cref="ITrackedChanges"/> (TF-062 AC2, ADR-0051).
/// </summary>
public static class ModelCheck
{
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
}
