namespace Iron.Architecture.Tests;

/// <summary>
/// One project file: its name, where it is, what role it plays, and the project references it declares.
/// </summary>
/// <param name="Name">The project name, for example <c>Iron.Identity</c>.</param>
/// <param name="RelativePath">The project file path from the repository root, with forward slashes.</param>
/// <param name="Kind">The role of the project.</param>
/// <param name="ModuleName">For modules, products and Contracts projects: the module's project name. Otherwise null.</param>
/// <param name="References">The names of the projects it references.</param>
public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    ProjectKind Kind,
    string? ModuleName,
    IReadOnlyList<string> References);
