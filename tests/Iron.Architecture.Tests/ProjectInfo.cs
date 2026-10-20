namespace Iron.Architecture.Tests;

/// <summary>
/// One project file: its name, where it is, the layer it declares, what role it plays, and the project references it declares.
/// </summary>
/// <param name="Name">The project name, for example <c>Iron.Kernel.Identity</c>.</param>
/// <param name="RelativePath">The project file path from the repository root, with forward slashes.</param>
/// <param name="DeclaredLayer">The text of &lt;IronLayer&gt; as written, or null when the project declares none.</param>
/// <param name="Layer">The declared layer, or null when it is missing or not a layer.</param>
/// <param name="Kind">The role of the project.</param>
/// <param name="ModuleName">For modules and Contracts projects: the module's project name. Otherwise null.</param>
/// <param name="References">The names of the projects it references.</param>
/// <param name="DeclaredSuite">The text of &lt;IronSuite&gt; as written, for example <c>Procurement</c>, or null when the project declares none (ADR-0043).</param>
public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    string? DeclaredLayer,
    ProjectLayer? Layer,
    ProjectKind Kind,
    string? ModuleName,
    IReadOnlyList<string> References,
    string? DeclaredSuite = null);
