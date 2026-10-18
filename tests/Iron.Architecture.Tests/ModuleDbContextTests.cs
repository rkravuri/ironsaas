using System.Reflection;
using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Architecture.Tests;

/// <summary>
/// Finds every ModuleDbContext in the module assemblies of the three layers, builds its model without a database, and
/// applies the model rules (TF-220: a business entity without an owning organisation fails the build). The rule for
/// business records applies to the Business and Products layers, and a context's schema carries the prefix of its
/// project's layer (ADR-0029).
/// </summary>
public class ModuleDbContextTests
{
    [Fact]
    public void EveryModuleDbContextPassesTheModelCheck()
    {
        var projects = ProjectCatalog.Load(Repository.Root)
            .Where(p => p.Kind == ProjectKind.Module)
            .ToList();

        var violations = new List<string>();
        var contextsFound = 0;
        foreach (var project in projects)
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue; // AssemblyReferenceTests already fails for a missing assembly
            }

            var assembly = Assembly.LoadFrom(assemblyPath);
            foreach (var contextType in ModuleDbContextTypes(assembly))
            {
                contextsFound++;
                violations.AddRange(CheckContext(contextType, isBusinessModule: project.Layer is ProjectLayer.Business or ProjectLayer.Products));
                if (project.Layer is { } layer && SchemaPrefixViolation(contextType.Name, SchemaAttribute.For(contextType), layer) is { } wrongPrefix)
                {
                    violations.Add(wrongPrefix);
                }
            }
        }

        ArchitectureAssert.NoViolations($"Model rules over {contextsFound} module context(s) (TF-220, ADR-0029)", violations);
    }

    [Theory]
    [InlineData("kernel_identity", ProjectLayer.Kernel, true)]
    [InlineData("business_items", ProjectLayer.Business, true)]
    [InlineData("products_procurement", ProjectLayer.Products, true)]
    [InlineData("identity", ProjectLayer.Kernel, false)]
    [InlineData("business_items", ProjectLayer.Kernel, false)]
    [InlineData("kernel_items", ProjectLayer.Business, false)]
    public void AContextsSchemaCarriesThePrefixOfItsProjectsLayer(string schema, ProjectLayer layer, bool accepted)
    {
        var violation = SchemaPrefixViolation("SampleDbContext", schema, layer);

        if (accepted)
        {
            Assert.Null(violation);
        }
        else
        {
            Assert.NotNull(violation);
            Assert.StartsWith($"SampleDbContext: the schema '{schema}' must start with '{ProjectLayers.SchemaPrefix(layer)}'", violation, StringComparison.Ordinal);
        }
    }

    /// <summary>A module context's schema starts with the prefix of the layer its project declares (ADR-0029).</summary>
    internal static string? SchemaPrefixViolation(string contextName, string schema, ProjectLayer layer)
    {
        var prefix = ProjectLayers.SchemaPrefix(layer);
        if (prefix is null || (schema.StartsWith(prefix, StringComparison.Ordinal) && schema.Length > prefix.Length))
        {
            return null;
        }

        return $"{contextName}: the schema '{schema}' must start with '{prefix}', the prefix of the {layer} layer its project declares (ADR-0029).";
    }

    private static IEnumerable<Type> ModuleDbContextTypes(Assembly assembly)
    {
        return assembly.GetTypes().Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ModuleDbContext).IsAssignableFrom(t));
    }

    /// <summary>Builds the context with its generic options, as a host would, but against a connection string that is never opened.</summary>
    private static IReadOnlyList<string> CheckContext(Type contextType, bool isBusinessModule)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        builder.UseNpgsql("Host=localhost;Database=model_check;Username=model;Password=check");

        using var context = (ModuleDbContext)Activator.CreateInstance(contextType, builder.Options)!;
        return ModelCheck.Check(context.Model, contextType.Name, isBusinessModule);
    }
}
