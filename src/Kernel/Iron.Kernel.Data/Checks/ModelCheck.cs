using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Iron.Kernel.Data.Checks;

/// <summary>
/// Checks an Entity Framework model before any database exists: every mapped type derives from <see cref="Entity"/>,
/// and in a business module (a product) every entity is a <see cref="BusinessRecord"/> unless it is marked
/// <see cref="ITenantWide"/> on purpose (TF-220: the build fails when a business entity lacks the owning organisation).
/// In every module, a business record is never also ITenantWide, which would let it page without its policy (ADR-0032).
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
            if (entityType.IsOwned())
            {
                continue;
            }

            var clrType = entityType.ClrType;
            if (!typeof(Entity).IsAssignableFrom(clrType))
            {
                violations.Add($"{contextName}: {clrType.Name} does not derive from Entity, AuditedEntity, AppendOnlyEntity or BusinessRecord.");
                continue;
            }

            if (typeof(BusinessRecord).IsAssignableFrom(clrType) && typeof(ITenantWide).IsAssignableFrom(clrType))
            {
                violations.Add(
                    $"{contextName}: {clrType.Name} is a BusinessRecord marked ITenantWide. A business record is read and paged through its record policy; remove ITenantWide (ADR-0032).");
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
