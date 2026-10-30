using Iron.Kernel;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Suites.Procurement.Purchasing.Contracts;
using Iron.Suites.Procurement.Purchasing.Data;
using Iron.Suites.Procurement.Purchasing.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Iron.Suites.Procurement.Purchasing.Api;

/// <summary>When and how the suite was installed in this tenant, and when an update last migrated and seeded it.</summary>
public sealed record SuiteInstallationResponse(DateTimeOffset InstalledAt, Guid? InstalledByJob, string InstalledVersion, DateTimeOffset SeededAt, Guid? SeededByJob, string SeededVersion);

/// <summary>One of the suite's licensed modules or features in this tenant: what applies now, why, and the dates.</summary>
public sealed record SuiteEntitlementResponse(string Code, string Module, string? Description, EntitlementStatus Status, string Reason, DateOnly? StartsOn, DateOnly? EndsOn, DateOnly? GraceEndsOn);

/// <summary>
/// What of the eProcurement Suite this tenant has (TF-244): the suite, the release the unit runs, the installation,
/// the plan the tenant holds for the suite with its grace days, and the suite's modules and features with their
/// entitlement. Read only.
/// </summary>
public sealed record SuiteAboutResponse(
    string Suite,
    string Name,
    string Description,
    string Version,
    SuiteInstallationResponse? Installation,
    string? Plan,
    int GraceDays,
    bool Stale,
    IReadOnlyList<SuiteEntitlementResponse> Modules,
    IReadOnlyList<SuiteEntitlementResponse> Features);

/// <summary>The suite's About endpoint (TF-244 AC1): an administrator sees exactly what the tenant has of the suite.</summary>
internal static class AboutEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapApiGroup("suites").MapGet("/procurement/about", AboutAsync).RequirePermission(PurchasingPermissions.SuiteRead).Produces<SuiteAboutResponse>(StatusCodes.Status200OK).WithName("GetProcurementSuiteAbout").WithSummary("What of the eProcurement Suite is installed for this tenant: the release, when and by which job it was installed, the plan, and the suite's modules and features with their entitlement.");
    }

    private static async Task<IResult> AboutAsync(PurchasingDbContext context, ISuiteSubscriptions subscriptions, UnitSuites suites, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var suite = suites.Find(ProcurementSuite.ShortName);
        var installation = await context.Installations.AsNoTracking().SingleOrDefaultAsync(i => i.Suite == ProcurementSuite.ShortName, cancellationToken);
        var subscription = subscriptions.Of(ProcurementSuite.ShortName);
        return TypedResults.Ok(new SuiteAboutResponse(
            ProcurementSuite.ShortName,
            suite?.DisplayName ?? ProcurementSuite.DisplayName,
            suite?.Description ?? ProcurementSuite.Description,
            SuiteInstallationSeeder.VersionOf(configuration),
            installation is null ? null : new SuiteInstallationResponse(installation.InstalledAt, installation.InstalledByJob, installation.InstalledVersion, installation.SeededAt, installation.SeededByJob, installation.SeededVersion),
            subscription.Plan,
            subscription.GraceDays,
            subscription.Stale,
            [.. subscription.Modules.Select(Response)],
            [.. subscription.Features.Select(Response)]));
    }

    private static SuiteEntitlementResponse Response(SubscribedEntitlement entry) =>
        new(entry.Code, entry.Module, entry.Description, entry.Status, entry.Reason, entry.StartsOn, entry.EndsOn, entry.GraceEndsOn);
}
