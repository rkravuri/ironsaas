using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Sharing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Api;

/// <summary>A unit as the sharing control names it.</summary>
public sealed record SharingUnitResponse(Guid Id, string Name, string Code);

/// <summary>A unit the record may be shared with, placed on the tree.</summary>
public sealed record SharingChoiceResponse(Guid Id, string Name, string Code, Guid? ParentId, int Depth);

/// <summary>
/// A record's sharing (TF-222, ADR-0038): its own unit, the mode, the units it is listed for when shared, and whether
/// the reader may change it, so the record page shows the control to change or only the words. For a reader who may
/// change it, the active units of the tenant in tree order, to choose from: whoever may share a record may see the
/// names of the units it can be shared with, though they may not read the tree otherwise.
/// </summary>
public sealed record RecordSharingResponse(string RecordType, Guid RecordId, SharingUnitResponse OwningUnit, SharingMode Mode, IReadOnlyList<SharingUnitResponse> Units, bool MayChange, IReadOnlyList<SharingChoiceResponse> Choices);

public sealed record ChangeSharingRequest(SharingMode Mode, IReadOnlyList<Guid>? Units);

public sealed class ChangeSharingRequestValidator : AbstractValidator<ChangeSharingRequest>
{
    public ChangeSharingRequestValidator()
    {
        RuleFor(r => r.Mode).IsInEnum();
        RuleFor(r => r.Units).Must(units => units is null || units.Count <= 200).WithMessage("List at most 200 units.");
    }
}

/// <summary>
/// The sharing of any business record with a policy, by its record type and id (ADR-0038). Each endpoint needs a
/// signed-in principal; the record's own policy decides the rest: reading the sharing needs the record to be readable
/// (an unreadable record is not found), and changing it is the policy action <c>share</c>. Authorization maps these
/// for the data library, which knows records but has no endpoints, and names the units.
/// </summary>
internal static class RecordSharingEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var sharing = endpoints.MapApiGroup("record-sharing");
        sharing.MapGet("/{recordType}/{id:guid}", GetAsync).RequireSignedIn().WithName("GetRecordSharing").WithSummary("Who sees this record beyond its unit: private, shared with listed units, or the whole tenant.");
        sharing.MapPut("/{recordType}/{id:guid}", ChangeAsync).RequireSignedIn().Validate<ChangeSharingRequest>().WithName("ChangeRecordSharing").WithSummary("Change who sees this record beyond its unit; the record's policy decides who may. Audited with the units before and after.");
    }

    private static async Task<IResult> GetAsync(string recordType, Guid id, HttpContext http, IRecordSharing sharing, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var result = await sharing.GetAsync(recordType, id, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await DescribeAsync(result.Value, context, cancellationToken));
    }

    private static async Task<IResult> ChangeAsync(string recordType, Guid id, ChangeSharingRequest request, HttpContext http, IRecordSharing sharing, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var result = await sharing.ChangeAsync(recordType, id, request.Mode, request.Units ?? [], cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await DescribeAsync(result.Value, context, cancellationToken));
    }

    private static async Task<RecordSharingResponse> DescribeAsync(RecordSharingView view, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var ids = view.Units.Append(view.OwningUnitId).ToArray();
        var units = await context.OrganisationUnits.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        SharingUnitResponse Named(Guid unitId) => units.TryGetValue(unitId, out var unit) ? new SharingUnitResponse(unit.Id, unit.Name, unit.Code) : new SharingUnitResponse(unitId, string.Empty, string.Empty);
        IReadOnlyList<SharingChoiceResponse> choices = [];
        if (view.MayChange)
        {
            var active = await context.OrganisationUnits.AsNoTracking().Where(u => u.Status == UnitStatus.Active).ToListAsync(cancellationToken);
            choices = [.. OrganisationUnitService.InTreeOrder(active).Select(u => new SharingChoiceResponse(u.Id, u.Name, u.Code, u.ParentId, u.Depth))];
        }

        return new RecordSharingResponse(view.RecordType, view.RecordId, Named(view.OwningUnitId), view.Mode, [.. view.Units.Select(Named).OrderBy(u => u.Name, StringComparer.CurrentCulture)], view.MayChange, choices);
    }
}
