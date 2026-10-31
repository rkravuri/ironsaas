using Iron.Kernel;

namespace Iron.Suites.Procurement.Purchasing.Contracts;

/// <summary>The eProcurement Suite was installed in this tenant, at its first seed (TF-244): with which release, by which job.</summary>
public sealed record SuiteInstalled(Guid InstallationId, string Suite, string Release, Guid? JobId);

/// <summary>
/// Purchasing's domain events (TF-110, ADR-0060). They exist only where the suite is installed: a unit without the suite
/// runs no module of it, so it declares, stores and hands on none of them (TF-244).
/// </summary>
public static class PurchasingEvents
{
    public static readonly EventType<SuiteInstalled> SuiteInstalled = new(
        "purchasing.suite.installed", 1, "suite_installation", "The eProcurement Suite was installed in this tenant, at its first seed.", "Purchasing module");

    public static IReadOnlyList<EventType> All => [SuiteInstalled];
}
