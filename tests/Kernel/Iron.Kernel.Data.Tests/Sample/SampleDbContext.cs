using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Tests.Sample;

/// <summary>A module context as a real module would write it (see the Iron.Kernel.Data README).</summary>
[Schema("sample")]
public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<Preference> Preferences => Set<Preference>();

    public DbSet<WidgetEvent> WidgetEvents => Set<WidgetEvent>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Preference>().HasIndex(p => p.Key).IsUnique();
    }
}

/// <summary>A context that forgot its schema attribute, for the test of the error message.</summary>
public sealed class ContextWithoutSchema(DbContextOptions<ContextWithoutSchema> options) : ModuleDbContext(options)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}
