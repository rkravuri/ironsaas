namespace Iron.Architecture.Tests;

/// <summary>
/// The project reference rules of the modular monolith (PRD section 3.5, ADR-0001).
/// Every message names the project file to fix and the rule that was broken.
/// </summary>
public static class ModuleBoundaryRules
{
    public static IReadOnlyList<string> Check(IReadOnlyList<ProjectInfo> projects)
    {
        var byName = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            byName[project.Name] = project;
        }

        var violations = new List<string>();
        foreach (var project in projects)
        {
            foreach (var reference in project.References)
            {
                if (!byName.TryGetValue(reference, out var target))
                {
                    violations.Add(
                        $"{project.RelativePath}: references '{reference}', which is not a project in this repository.");
                    continue;
                }

                var reason = ReasonIfForbidden(project, target);
                if (reason is not null)
                {
                    violations.Add($"{project.RelativePath}: must not reference {target.RelativePath}. {reason}");
                }
            }
        }

        return violations;
    }

    /// <summary>Returns null when the reference is allowed, otherwise the rule that forbids it.</summary>
    public static string? ReasonIfForbidden(ProjectInfo project, ProjectInfo target)
    {
        switch (project.Kind)
        {
            case ProjectKind.Shared:
                return target.Kind == ProjectKind.Shared
                    ? null
                    : "A shared library may reference shared libraries only.";

            case ProjectKind.Contracts:
                return target.Name == "Iron.Kernel"
                    ? null
                    : "A Contracts project may reference Iron.Kernel only. Other modules are referenced by identifier, not by type.";

            case ProjectKind.Module:
            case ProjectKind.Product:
                return target.Kind is ProjectKind.Shared or ProjectKind.Contracts
                    ? null
                    : "A module may reference shared libraries and Contracts projects only. Another module's Api, Services and Data are private.";

            case ProjectKind.Host:
                return target.Kind is ProjectKind.Shared or ProjectKind.Contracts or ProjectKind.Module or ProjectKind.Product
                    ? null
                    : "A host may reference shared libraries, modules, products and Contracts projects only.";

            default:
                // Tests and tools are not constrained.
                return null;
        }
    }
}
