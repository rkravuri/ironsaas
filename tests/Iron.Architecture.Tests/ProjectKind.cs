namespace Iron.Architecture.Tests;

/// <summary>
/// The role a project plays inside its layer. The layer and the role decide which references it may have (ADR-0001, ADR-0029).
/// </summary>
public enum ProjectKind
{
    /// <summary>Iron.Kernel: the kernel vocabulary (results, errors, ids, tenant context, money, paging, principals, permissions).</summary>
    Vocabulary,

    /// <summary>Iron.Kernel.Data, .Api, .Telemetry, .Security: libraries every module may use.</summary>
    Library,

    /// <summary>The public surface of a module: interfaces and DTOs other modules may use.</summary>
    Contracts,

    /// <summary>A module of the kernel, business or products layer, with Api, Services and Data folders.</summary>
    Module,

    /// <summary>A host (Iron.Host.*) that composes modules into a runnable application.</summary>
    Host,

    /// <summary>A test project under tests/. It may reference what it tests.</summary>
    Test,

    /// <summary>A tool under tools/. It may reference what it drives.</summary>
    Tool,

    /// <summary>A project that declares no layer, or one that is not a layer. LayerRules names it.</summary>
    Unknown,
}
