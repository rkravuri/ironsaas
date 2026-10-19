namespace Iron.Architecture.Tests;

/// <summary>
/// The project reference rules of the modular monolith (ADR-0001) across the three layers (ADR-0029): dependencies go
/// downward only, and between modules only through a Contracts project. Every message names the project file to fix
/// and the rule that was broken.
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
        if (project.Kind is ProjectKind.Test or ProjectKind.Tool or ProjectKind.Unknown)
        {
            // Tests and tools may reference what they test or drive; a project without a layer is named by LayerRules.
            return null;
        }

        if (target.Kind == ProjectKind.Unknown)
        {
            return "It declares no valid IronLayer, so no reference to it can be checked (ADR-0029).";
        }

        var projectRank = project.Layer is { } projectLayer ? ProjectLayers.Rank(projectLayer) : null;
        var targetRank = target.Layer is { } targetLayer ? ProjectLayers.Rank(targetLayer) : null;
        if (projectRank is not null && targetRank is not null && targetRank > projectRank)
        {
            return $"Dependencies go downward only: a {project.Layer} project may not use the {target.Layer} layer. When the kernel needs what only Business can supply, the kernel declares an interface in its Contracts and Business implements it (ADR-0029).";
        }

        switch (project.Kind)
        {
            case ProjectKind.Vocabulary:
                return "The kernel vocabulary references nothing.";

            case ProjectKind.Library:
                return target.Kind is ProjectKind.Vocabulary or ProjectKind.Library
                    ? null
                    : "A kernel library may reference the vocabulary and other kernel libraries only.";

            case ProjectKind.Contracts:
                return target.Kind == ProjectKind.Vocabulary
                    ? null
                    : $"A Contracts project may reference {ProjectCatalog.Vocabulary} only. Other modules are referenced by identifier, not by type.";

            case ProjectKind.Module:
                return target.Kind is ProjectKind.Vocabulary or ProjectKind.Library or ProjectKind.Contracts
                    ? null
                    : "A module may reference the kernel vocabulary, the kernel libraries and Contracts projects only. Another module's Api, Services and Data are private.";

            case ProjectKind.Host:
                return target.Kind is ProjectKind.Vocabulary or ProjectKind.Library or ProjectKind.Contracts or ProjectKind.Module
                    ? null
                    : "A host may reference projects of the kernel, business and products layers only, never another host, a test or a tool.";

            default:
                return null;
        }
    }
}
