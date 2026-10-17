using System.Linq.Expressions;
using Iron.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iron.Data.Conventions;

/// <summary>
/// The model rules every module gets without writing them (TF-080): the key is <c>id</c> and is set in code;
/// <c>version</c> is the concurrency token; append-only tables carry the comment the schema check reads;
/// business records are indexed by owning organisation; soft-deleted rows are hidden by a query filter.
/// Names become snake_case through EFCore.NamingConventions, configured on the options.
/// </summary>
public static class ModelConventions
{
    public const string AppendOnlyComment = "append-only";

    public static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            if (entityType.IsOwned() || !typeof(Entity).IsAssignableFrom(clrType))
            {
                continue;
            }

            var entity = modelBuilder.Entity(clrType);
            entity.HasKey(nameof(Entity.Id));
            entity.Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (typeof(AuditedEntity).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(AuditedEntity.Version)).IsConcurrencyToken();
            }

            if (typeof(AppendOnlyEntity).IsAssignableFrom(clrType))
            {
                entity.ToTable(table => table.HasComment(AppendOnlyComment));
            }

            if (typeof(BusinessRecord).IsAssignableFrom(clrType))
            {
                entity.HasIndex(nameof(BusinessRecord.OwningOrganisationId));
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                entity.HasQueryFilter(NotDeleted(clrType));
            }
        }
    }

    // e => !e.IsDeleted, built for the concrete type because HasQueryFilter needs a typed lambda.
    private static LambdaExpression NotDeleted(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var isDeleted = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
        return Expression.Lambda(Expression.Not(isDeleted), parameter);
    }
}
