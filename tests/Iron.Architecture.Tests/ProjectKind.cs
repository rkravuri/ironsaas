namespace Iron.Architecture.Tests;

/// <summary>
/// The role a project plays in the repository. The role decides which references it may have (ADR-0001).
/// </summary>
public enum ProjectKind
{
    /// <summary>Iron.Kernel, Iron.Kernel.Data, Iron.Kernel.Api, Iron.Kernel.Telemetry, Iron.Kernel.Security: libraries every module may use.</summary>
    Shared,

    /// <summary>The public surface of a module: interfaces and DTOs other modules may use.</summary>
    Contracts,

    /// <summary>A Foundation module (Iron.Kernel.Identity, Iron.Kernel.Audit, ...) with Api, Services and Data folders.</summary>
    Module,

    /// <summary>A business product module under products/. Same rules as a Foundation module.</summary>
    Product,

    /// <summary>A host (Iron.Host.*) that composes modules into a runnable application.</summary>
    Host,

    /// <summary>A test project under tests/. Not constrained.</summary>
    Test,

    /// <summary>A tool under tools/. Not constrained.</summary>
    Tool,
}
