using System.Reflection;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.History;

namespace Iron.Architecture.Tests;

/// <summary>
/// Every audit event type reads as words (ADR-0051): each constant of a module's <c>*AuditEvents</c> class is named by a
/// module's <see cref="IModule.AuditEventTypes"/>, and each name is a short phrase in sentence case, not a code.
/// </summary>
public class AuditEventNamesTests
{
    [Fact]
    [Proves("TF-064 AC4", "ADR-0051")]
    public void EveryAuditEventConstantIsNamedByAModule()
    {
        var assemblies = ModuleAssemblies();
        var named = new AuditEventNames(Modules(assemblies).SelectMany(m => m.AuditEventTypes)).All.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        var constants = 0;
        foreach (var type in assemblies.SelectMany(a => a.GetExportedTypes()).Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("AuditEvents", StringComparison.Ordinal)))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                constants++;
                var code = (string)field.GetRawConstantValue()!;
                if (!named.Contains(code))
                {
                    violations.Add($"{type.FullName}.{field.Name} ('{code}') has no name: add it to the class's Types and its module's AuditEventTypes.");
                }
            }
        }

        Assert.True(constants > 100, $"Only {constants} audit event constants were found; the search lost its way.");
        ArchitectureAssert.NoViolations($"Names of {constants} audit event types (ADR-0051)", violations);
    }

    [Fact]
    [Proves("TF-064 AC4", "ADR-0051")]
    public void EveryNameIsAShortPhraseInSentenceCase()
    {
        var violations = new List<string>();
        foreach (var type in Modules(ModuleAssemblies()).SelectMany(m => m.AuditEventTypes))
        {
            var name = type.Name;
            if (!char.IsUpper(name[0]) || name.Length > 60 || name.IndexOfAny(['.', '_', '!', '\u2014']) >= 0 || name.EndsWith(' '))
            {
                violations.Add($"'{type.Code}' reads as '{name}': a name is a short phrase in sentence case, without dots, underscores, dashes for pauses or exclamation marks.");
            }
        }

        ArchitectureAssert.NoViolations("Audit event names read as words (ADR-0051)", violations);
    }

    private static List<Assembly> ModuleAssemblies() =>
        [.. ProjectCatalog.Load(Repository.Root)
            .Where(p => p.Kind is ProjectKind.Module or ProjectKind.Contracts or ProjectKind.Library)
            .Select(p => Path.Combine(AppContext.BaseDirectory, p.Name + ".dll"))
            .Where(File.Exists)
            .Select(Assembly.LoadFrom)];

    private static IEnumerable<IModule> Modules(IEnumerable<Assembly> assemblies) =>
        assemblies.SelectMany(a => a.GetExportedTypes())
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IModule)Activator.CreateInstance(t)!);
}
