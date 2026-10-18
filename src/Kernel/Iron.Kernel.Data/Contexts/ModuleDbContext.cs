using Iron.Kernel.Data.Conventions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Contexts;

/// <summary>
/// Base of every module's DbContext. The module declares its schema with <see cref="SchemaAttribute"/>, maps its
/// entities in <see cref="ConfigureModule"/>, and the Foundation conventions are applied afterwards. Each context
/// keeps its own migrations history table inside its schema, so modules migrate independently (TF-080, TF-082).
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
        Schema = SchemaAttribute.For(GetType());
    }

    /// <summary>The module's schema, from its <see cref="SchemaAttribute"/>.</summary>
    public string Schema { get; }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModule(modelBuilder);
        ModelConventions.Apply(modelBuilder);
    }

    /// <summary>Map this module's entities here (keys, relations inside the module, indexes). Conventions run afterwards.</summary>
    protected abstract void ConfigureModule(ModelBuilder modelBuilder);
}
