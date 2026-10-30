using Iron.Kernel;
using Iron.Kernel.History;

namespace Iron.Suites.Procurement.Purchasing.Contracts;

/// <summary>
/// The eProcurement Suite as hosts compose it and its pages name it (ADR-0043, ADR-0059): its short name, which its
/// schemas, its shell folder, the catalogue and a unit's configuration use, and its name and description for people.
/// </summary>
public static class ProcurementSuite
{
    public const string ShortName = "procurement";

    public const string DisplayName = "eProcurement Suite";

    public const string Description = "Source to pay for regulated industries: supplier management, requests and intake, catalogues, orders, receiving, invoicing and contracts, with one portal for suppliers.";

    /// <summary>The suite's starter plan (TF-244): the Purchasing module, which every plan of the suite includes.</summary>
    public const string EssentialsPlan = "procurement-essentials";
}

/// <summary>
/// The Purchasing module's licence (ADR-0034, ADR-0058): the suite's shared master, which every plan of the suite
/// includes, sold with the suite rather than per person.
/// </summary>
public static class PurchasingLicence
{
    public const string Code = "purchasing";
}

/// <summary>The Purchasing module's permissions (TF-030): today only the suite's About page.</summary>
public static class PurchasingPermissions
{
    /// <summary>What of the suite is installed and under which plan: an administrator's view, nothing a buyer changes.</summary>
    public static readonly Permission SuiteRead = new("purchasing.suite.read", "View what of the eProcurement Suite is installed for the organisation, and under which plan", Risk.Low);

    public static IReadOnlyList<Permission> All => [SuiteRead];
}

/// <summary>The Purchasing module's audit events (ADR-0013), each with its words (ADR-0051).</summary>
public static class PurchasingAuditEvents
{
    /// <summary>The suite's schema was migrated and seeded in this tenant for the first time (TF-244 AC1, AC3).</summary>
    public const string SuiteInstalled = "purchasing.suite_installed";

    /// <summary>An update migrated and seeded the suite again, with the release it brought and the job that ran it.</summary>
    public const string SuiteSeeded = "purchasing.suite_seeded";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        // Each event's subject names the suite.
        new AuditEventType(SuiteInstalled, "Suite installed").AsAdministrative(),
        new AuditEventType(SuiteSeeded, "Suite migrated and seeded again").AsAdministrative(),
    ];
}
