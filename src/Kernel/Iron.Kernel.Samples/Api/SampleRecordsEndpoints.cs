using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Samples.Contracts;
using Iron.Kernel.Samples.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Samples.Api;

public sealed record SampleRecordResponse(Guid Id, string Name, Guid OwningUnitId, SharingMode SharingMode, IReadOnlyList<string> VisibilityTags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version, DateTimeOffset? NoticedAt)
{
    public static SampleRecordResponse From(SampleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(record.Id, record.Name, record.OwningOrganisationId, record.SharingMode, record.VisibilityTags, record.CreatedAt, record.UpdatedAt, record.Version, record.NoticedAt);
    }
}

/// <summary>A new sample record: its name, and its unit when not the creator's working unit.</summary>
public sealed record CreateSampleRecordRequest(string Name, Guid? UnitId);

public sealed record RenameSampleRecordRequest(string Name);

public sealed class CreateSampleRecordRequestValidator : AbstractValidator<CreateSampleRecordRequest>
{
    public CreateSampleRecordRequestValidator() => RuleFor(r => r.Name).NotEmpty().MaximumLength(SampleRecord.NameMaxLength);
}

public sealed class RenameSampleRecordRequestValidator : AbstractValidator<RenameSampleRecordRequest>
{
    public RenameSampleRecordRequestValidator() => RuleFor(r => r.Name).NotEmpty().MaximumLength(SampleRecord.NameMaxLength);
}

/// <summary>
/// Sample records (ADR-0057): listed and opened only through their policy, so the scope rule, the sharing and the reader's
/// visibility tags apply as they will to every product's records; created at a unit where the creator may manage them.
/// </summary>
internal static class SampleRecordsEndpoints
{
    public static readonly ListFields<SampleRecord> Fields = new ListFields<SampleRecord>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("name", r => r.Name)
        .Add("owning_unit_id", r => r.OwningOrganisationId, FieldUse.Select | FieldUse.Filter)
        .Add("updated_at", r => r.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("name");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var records = endpoints.MapApiGroup("sample-records");
        records.MapGet("/", ListAsync).RequirePermission(SamplesPermissions.RecordsRead).ScopedByRow().Produces<ListResponse<SampleRecordResponse>>(StatusCodes.Status200OK).WithName("ListSampleRecords").WithSummary("The sample records you may see: your units', those shared with them, and those of your visibility tags.");
        records.MapPost("/", CreateAsync).RequirePermission(SamplesPermissions.RecordsManage).ScopedByRow().Validate<CreateSampleRecordRequest>().Produces<SampleRecordResponse>(StatusCodes.Status201Created).WithName("CreateSampleRecord").WithSummary("Create a sample record at your working unit, or at the unit named.");
        records.MapGet("/{id:guid}", GetAsync).RequirePermission(SamplesPermissions.RecordsRead).ScopedToRecord(SampleRecord.RecordType, OwningUnitAsync, policyAction: RecordActions.Read).Produces<SampleRecordResponse>(StatusCodes.Status200OK).WithName("GetSampleRecord").WithSummary("One sample record you may see.");
        records.MapPut("/{id:guid}", RenameAsync).RequirePermission(SamplesPermissions.RecordsManage).ScopedToRecord(SampleRecord.RecordType, OwningUnitAsync, policyAction: RecordActions.Update).Validate<RenameSampleRecordRequest>().Produces<SampleRecordResponse>(StatusCodes.Status200OK).WithName("RenameSampleRecord").WithSummary("Rename a sample record.");
    }

    /// <summary>The unit that owns a record, for the permission check of a record route; null for one that does not exist.</summary>
    public static async Task<Guid?> OwningUnitAsync(IServiceProvider services, Guid id, CancellationToken cancellationToken) =>
        await services.GetRequiredService<SamplesDbContext>().Records.AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.OwningOrganisationId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ListAsync(HttpContext http, SamplesDbContext context, IRecordPolicies policies, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var policed = await policies.ApplyAsync(context.Records.AsNoTracking(), cancellationToken);
        var page = await policed.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(SampleRecordResponse.From), http, query);
    }

    private static async Task<IResult> CreateAsync(CreateSampleRecordRequest request, HttpContext http, SamplesDbContext context, ICurrentPrincipal current, IPermissionEvaluator evaluator, IUnitOfWork unitOfWork, IEventPublisher events, CancellationToken cancellationToken)
    {
        if (NewRecords.OwningUnit(current, request.UnitId) is not { } unit)
        {
            return ApiResults.Problem(Error.Validation(SamplesErrors.UnitRequired, "Name the unit the record belongs to.", new FieldError("unit_id", "required", "Choose a unit.")), http);
        }

        // The permission is checked at the unit the record will belong to, which the route does not carry (ADR-0031).
        var check = new AccessRequest(current.Id, SamplesPermissions.RecordsManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = AccessScope.Unit(unit) };
        if (!(await evaluator.EvaluateAsync(check, cancellationToken)).Allowed)
        {
            return ApiResults.Problem(Error.Forbidden(PermissionErrors.PermissionDenied, $"You do not hold {SamplesPermissions.RecordsManage.Name} at this unit."), http);
        }

        // The record and its event commit together (ADR-0060); the module's own subscriber notices it after the commit.
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var record = new SampleRecord(unit, request.Name);
        context.Records.Add(record);
        await context.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(SamplesEvents.RecordCreated, record.Id, new SampleRecordCreated(record.Id, unit), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"{ApiRouting.Prefix}/sample-records/{record.Id}", SampleRecordResponse.From(record));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IRecordPolicies policies, CancellationToken cancellationToken)
    {
        var found = await policies.FindAsync<SampleRecord>(id, RecordActions.Read, cancellationToken);
        return found.IsFailure ? ApiResults.Problem(found.Error, http) : TypedResults.Ok(SampleRecordResponse.From(found.Value));
    }

    private static async Task<IResult> RenameAsync(Guid id, RenameSampleRecordRequest request, HttpContext http, IRecordPolicies policies, SamplesDbContext context, CancellationToken cancellationToken)
    {
        var found = await policies.FindAsync<SampleRecord>(id, RecordActions.Update, cancellationToken);
        if (found.IsFailure)
        {
            return ApiResults.Problem(found.Error, http);
        }

        found.Value.Rename(request.Name);
        await context.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(SampleRecordResponse.From(found.Value));
    }
}
