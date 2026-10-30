using Iron.Kernel.History;

namespace Iron.Kernel.Entitlements.Contracts;

/// <summary>
/// The one effective-entitlement service of a unit (TF-043): what product code and the shell ask. A kernel module is
/// always on and is never asked about; a licensed module and a feature answer Enabled, ReadOnly or Disabled with the
/// reason. A feature of a module that is not available is unavailable whatever its own status (TF-046 AC3).
/// </summary>
public interface IEntitlements
{
    /// <summary>A licensed module for the whole tenant, by its licence code; seats are not looked at.</summary>
    Entitlement Module(string licence);

    /// <summary>A licensed module for one person: a per-person module needs the person's seat as well (TF-041).</summary>
    Task<Entitlement> ModuleForAsync(string licence, Guid principalId, CancellationToken cancellationToken);

    /// <summary>A feature by its code, governed by its module.</summary>
    Entitlement Feature(string feature);
}

/// <summary>One licensed module or feature of a suite as the unit holds it: what applies now, why, and the subscription's dates.</summary>
public sealed record SubscribedEntitlement(string Code, string Module, string? Description, EntitlementStatus Status, string Reason, DateOnly? StartsOn, DateOnly? EndsOn, DateOnly? GraceEndsOn);

/// <summary>
/// A suite's subscription in this unit (TF-244, ADR-0059): the plan the tenant holds for it and its grace days, and each
/// of the suite's licensed modules and features this unit loaded, with what applies now. Stale when the unit could not
/// confirm its entitlements, as the context says.
/// </summary>
public sealed record SuiteSubscription(string Suite, string? Plan, int GraceDays, bool Stale, IReadOnlyList<SubscribedEntitlement> Modules, IReadOnlyList<SubscribedEntitlement> Features);

/// <summary>A suite's subscription for the suite's own pages, such as its About page (TF-244); Entitlements implements it.</summary>
public interface ISuiteSubscriptions
{
    SuiteSubscription Of(string suite);
}

public static class EntitlementsPermissions
{
    public static readonly Permission SubscriptionRead = new("entitlements.subscription.read", "View the tenant's subscription: modules, features and seats", Risk.Low);

    public static readonly Permission SeatsRead = new("entitlements.seats.read", "See who holds a seat of a per-person module", Risk.Low);

    public static readonly Permission SeatsManage = new("entitlements.seats.manage", "Assign and remove seats of per-person modules", Risk.High);

    public static IReadOnlyList<Permission> All => [SubscriptionRead, SeatsRead, SeatsManage];
}

/// <summary>The codes of the module's own services. The refusals of the entitlement check are in Iron.Kernel.Api (<c>EntitlementErrors</c>).</summary>
public static class EntitlementsErrors
{
    public const string SeatNotPerPerson = "entitlements.seat.not_per_person";
    public const string SeatNotLicensed = "entitlements.seat.not_licensed";
    public const string SeatLimit = "entitlements.seat.limit";
    public const string SeatNotFound = "entitlements.seat.not_found";
    public const string SeatPrincipal = "entitlements.seat.principal";
}

/// <summary>Audit event types raised by Entitlements (TF-060).</summary>
public static class EntitlementsAuditEvents
{
    public const string AccessDenied = "entitlements.access_denied";
    public const string SeatAssigned = "entitlements.seat_assigned";
    public const string SeatUnassigned = "entitlements.seat_unassigned";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(AccessDenied, "Refused by the licence"),
        new AuditEventType(SeatAssigned, "Seat assigned").AsAdministrative(),
        new AuditEventType(SeatUnassigned, "Seat removed").AsAdministrative(),
    ];
}
