using System.Reflection;
using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Architecture.Tests;

/// <summary>
/// Finds every ModuleDbContext in the module and product assemblies, builds its model without a database, and
/// applies the model rules (TF-220: a business entity without an owning organisation fails the build).
/// </summary>
public class ModuleDbContextTests
{
    [Fact]
    public void EveryModuleDbContextPassesTheModelCheck()
    {
        var projects = ProjectCatalog.Load(Repository.Root)
            .Where(p => p.Kind is ProjectKind.Module or ProjectKind.Product)
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
                violations.AddRange(CheckContext(contextType, isBusinessModule: project.Kind == ProjectKind.Product));
            }
        }

        ArchitectureAssert.NoViolations($"Model rules over {contextsFound} module context(s) (TF-220)", violations);
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
