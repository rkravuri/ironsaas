using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>A unit's alert as it sends it, after every change; its id is in the path.</summary>
public sealed record AlertCopyRequest(
    int Revision,
    string Rule,
    string Title,
    string Kind,
    string Severity,
    string State,
    string GroupKind,
    string GroupValue,
    long Count,
    int Threshold,
    int WindowMinutes,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt,
    DateTimeOffset RaisedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    string? Reason,
    string? Link,
    string? Runbook);

public sealed class AlertCopyRequestValidator : AbstractValidator<AlertCopyRequest>
{
    public AlertCopyRequestValidator()
    {
        RuleFor(r => r.Revision).InclusiveBetween(1, int.MaxValue);
        RuleFor(r => r.Rule).NotEmpty().MaximumLength(60);
        RuleFor(r => r.Title).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Kind).NotEmpty().MaximumLength(20);
        RuleFor(r => r.Severity).NotEmpty().MaximumLength(20);
        RuleFor(r => r.State).NotEmpty().MaximumLength(20);
        RuleFor(r => r.GroupKind).NotEmpty().MaximumLength(20);
        RuleFor(r => r.GroupValue).NotEmpty().MaximumLength(500);
        RuleFor(r => r.Count).InclusiveBetween(0, long.MaxValue);
        RuleFor(r => r.Threshold).InclusiveBetween(0, 1_000_000);
        RuleFor(r => r.WindowMinutes).InclusiveBetween(0, 10_080);
        RuleFor(r => r.Reason).MaximumLength(1000);
        RuleFor(r => r.Link).MaximumLength(2000);
        RuleFor(r => r.Runbook).MaximumLength(200);
    }
}

public sealed record AffectedTenantRequest(string Tenant, bool PersonalData, DateTimeOffset? KnownAt);

public sealed class AffectedTenantRequestValidator : AbstractValidator<AffectedTenantRequest>
{
    public AffectedTenantRequestValidator() => RuleFor(r => r.Tenant).NotEmpty().MaximumLength(63);
}

public sealed record DeclareIncidentRequest(int Severity, string? Summary, string? Lead, string? Communications, string? Scribe, IReadOnlyList<AffectedTenantRequest>? Tenants);

public sealed class DeclareIncidentRequestValidator : AbstractValidator<DeclareIncidentRequest>
{
    public DeclareIncidentRequestValidator()
    {
        RuleFor(r => r.Severity).InclusiveBetween(IncidentService.MinimumSeverity, IncidentService.MaximumSeverity);
        RuleFor(r => r.Summary).MaximumLength(Incident.SummaryMaxLength);
        RuleFor(r => r.Lead).MaximumLength(Incident.NameMaxLength);
        RuleFor(r => r.Communications).MaximumLength(Incident.NameMaxLength);
        RuleFor(r => r.Scribe).MaximumLength(Incident.NameMaxLength);
        // For more, name none and say in the summary that the whole platform is affected.
        RuleFor(r => r.Tenants).MaximumCount(100);
        RuleForEach(r => r.Tenants).SetValidator(new AffectedTenantRequestValidator());
    }
}

public sealed record IncidentEntryRequest(string? Kind, string? Text, string? Tenant);

public sealed class IncidentEntryRequestValidator : AbstractValidator<IncidentEntryRequest>
{
    public IncidentEntryRequestValidator()
    {
        RuleFor(r => r.Kind).MaximumLength(20);
        RuleFor(r => r.Text).MaximumLength(Incident.TextMaxLength);
        RuleFor(r => r.Tenant).MaximumLength(63);
    }
}

public sealed record CloseIncidentRequest(string? Summary);

public sealed class CloseIncidentRequestValidator : AbstractValidator<CloseIncidentRequest>
{
    public CloseIncidentRequestValidator() => RuleFor(r => r.Summary).MaximumLength(Incident.TextMaxLength);
}

public sealed record IncidentReviewRequest(string? WhatHappened, string? Why, string? WhatChanges);

public sealed class IncidentReviewRequestValidator : AbstractValidator<IncidentReviewRequest>
{
    public IncidentReviewRequestValidator()
    {
        RuleFor(r => r.WhatHappened).MaximumLength(Incident.TextMaxLength);
        RuleFor(r => r.Why).MaximumLength(Incident.TextMaxLength);
        RuleFor(r => r.WhatChanges).MaximumLength(Incident.TextMaxLength);
    }
}

/// <summary>
/// A tenant an incident affects, with the notice's deadline when its personal data is affected: <c>due</c> until it is
/// told, <c>notified</c> or <c>notified_late</c> after, <c>overdue</c> from the deadline on, untold, which is when it is escalated.
/// </summary>
public sealed record IncidentTenantResponse(string Tenant, bool PersonalData, DateTimeOffset? KnownAt, DateTimeOffset? NoticeDueAt, DateTimeOffset? NotifiedAt, string? Notice, string NoticeState, DateTimeOffset? EscalatedAt);

public sealed record IncidentEntryResponse(Guid Id, DateTimeOffset At, string By, string Kind, string Text, string? Tenant);

public sealed record IncidentReviewResponse(DateTimeOffset RecordedAt, string WhatHappened, string Why, string WhatChanges, bool OnTime);

public sealed record IncidentResponse(
    Guid Id,
    int Severity,
    string Summary,
    string Lead,
    string? Communications,
    string? Scribe,
    string Status,
    DateTimeOffset DeclaredAt,
    DateTimeOffset? ClosedAt,
    string? CloseSummary,
    DateTimeOffset? ReviewDueAt,
    string ReviewState,
    IncidentReviewResponse? Review,
    IReadOnlyList<IncidentTenantResponse> Tenants,
    IReadOnlyList<IncidentEntryResponse> Timeline);

public sealed record IncidentSummaryResponse(Guid Id, int Severity, string Summary, string Lead, string Status, DateTimeOffset DeclaredAt, DateTimeOffset? ClosedAt, DateTimeOffset? ReviewDueAt, bool Reviewed);

/// <summary>
/// The units' security alerts as they arrive (ADR-0056), and incidents with their runbook (TF-079 AC2): declared, noted,
/// told to the tenants, closed and reviewed by operators; <c>make incident-declare</c> and its siblings drive them.
/// </summary>
internal static class IncidentEndpoints
{
    public static readonly ListFields<Incident> Fields = new ListFields<Incident>()
        .Add("id", i => i.Id, FieldUse.Select | FieldUse.Filter)
        .Add("severity", i => i.Severity, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("status", i => i.Status, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("declared_at", i => i.DeclaredAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .SortByDefault("declared_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var copies = endpoints.MapApiGroup("alert-copies");
        copies.MapPut("/{id:guid}", KeepCopyAsync).RequirePermission(TenancyPermissions.AlertCopiesAppend).Validate<AlertCopyRequest>().Produces(StatusCodes.Status204NoContent).WithName("KeepAlertCopy").WithSummary("A unit sends one of its security alerts after it changed; the operator's alerting follows it. Every copy is the sending unit's tenant's.");

        var incidents = endpoints.MapApiGroup("incidents");
        incidents.MapGet("/", ListAsync).RequirePermission(TenancyPermissions.IncidentsRead).Produces<ListResponse<IncidentSummaryResponse>>(StatusCodes.Status200OK).WithName("ListIncidents").WithSummary("The incidents, newest first.");
        incidents.MapGet("/{id:guid}", GetAsync).RequirePermission(TenancyPermissions.IncidentsRead).Produces<IncidentResponse>(StatusCodes.Status200OK).WithName("GetIncident").WithSummary("An incident with its tenants, each notice's 72-hour deadline, its timeline and its review.");
        incidents.MapPost("/", DeclareAsync).RequirePermission(TenancyPermissions.IncidentsManage).Validate<DeclareIncidentRequest>().Produces<IncidentResponse>(StatusCodes.Status201Created).WithName("DeclareIncident").WithSummary("Declare an incident: its severity, the tenants affected (and whether their personal data is, since when), its lead and a summary.");
        incidents.MapPost("/{id:guid}/entries", NoteAsync).RequirePermission(TenancyPermissions.IncidentsManage).Validate<IncidentEntryRequest>().Produces<IncidentResponse>(StatusCodes.Status200OK).WithName("NoteIncident").WithSummary("Add to the incident's timeline: a note, a communication, or the breach notice sent to a tenant.");
        incidents.MapPost("/{id:guid}/tenants", AffectAsync).RequirePermission(TenancyPermissions.IncidentsManage).Validate<AffectedTenantRequest>().Produces<IncidentResponse>(StatusCodes.Status200OK).WithName("AffectIncidentTenant").WithSummary("Name a tenant affected, or its personal data affected: the notice's deadline runs from when that became known.");
        incidents.MapPost("/{id:guid}/close", CloseAsync).RequirePermission(TenancyPermissions.IncidentsManage).Validate<CloseIncidentRequest>().Produces<IncidentResponse>(StatusCodes.Status200OK).WithName("CloseIncident").WithSummary("Close the incident once every tenant whose personal data was affected is told; its review is due within 10 days.");
        incidents.MapPost("/{id:guid}/review", ReviewAsync).RequirePermission(TenancyPermissions.IncidentsManage).Validate<IncidentReviewRequest>().Produces<IncidentResponse>(StatusCodes.Status200OK).WithName("ReviewIncident").WithSummary("Record the post-incident review: what happened, why, and what changes.");
    }

    private static async Task<IResult> KeepCopyAsync(Guid id, AlertCopyRequest request, HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var tenant = await chains.TenantOfCallerAsync(cancellationToken);
        if (tenant is null)
        {
            return ApiResults.Problem(Error.Forbidden(TenancyErrors.AlertCopyNotAUnit, "Only a tenant's unit sends its security alerts, and only its own."), http);
        }

        var copies = http.RequestServices.GetRequiredService<IAlertCopies>();
        await copies.KeepAsync(tenant, new AlertCopy(id, request.Revision, request.Rule, request.Title, request.Kind, request.Severity, request.State, request.GroupKind, request.GroupValue, request.Count, request.Threshold, request.WindowMinutes, request.FirstEventAt, request.LastEventAt, request.RaisedAt, request.AcknowledgedAt, request.ResolvedAt, request.Reason, request.Link, request.Runbook), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListAsync(HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var page = await context.Incidents.AsNoTracking().ApplyListQuery(parsed.Value, Fields).ToPageAsync(parsed.Value.Page, cancellationToken);
        return ApiResults.List(page.Map(i => new IncidentSummaryResponse(i.Id, i.Severity, i.Summary, i.Lead, i.Status, i.DeclaredAt, i.ClosedAt, i.ReviewDueAt, i.ReviewRecordedAt is not null)), http, parsed.Value);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken) =>
        Answer(await incidents.RecordAsync(id, cancellationToken), http, time, created: false);

    private static async Task<IResult> DeclareAsync(DeclareIncidentRequest request, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenants = (request.Tenants ?? []).Select(t => new AffectedTenant(t.Tenant, t.PersonalData, t.KnownAt)).ToList();
        return Answer(await incidents.DeclareAsync(new IncidentDeclaration(request.Severity, request.Summary ?? string.Empty, request.Lead ?? string.Empty, request.Communications, request.Scribe, tenants), cancellationToken), http, time, created: true);
    }

    private static async Task<IResult> NoteAsync(Guid id, IncidentEntryRequest request, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken) =>
        Answer(await incidents.NoteAsync(id, request.Kind ?? IncidentEntry.Note, request.Text ?? string.Empty, string.IsNullOrWhiteSpace(request.Tenant) ? null : request.Tenant, cancellationToken), http, time, created: false);

    private static async Task<IResult> AffectAsync(Guid id, AffectedTenantRequest request, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken) =>
        Answer(await incidents.AffectAsync(id, new AffectedTenant(request.Tenant, request.PersonalData, request.KnownAt), cancellationToken), http, time, created: false);

    private static async Task<IResult> CloseAsync(Guid id, CloseIncidentRequest request, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken) =>
        Answer(await incidents.CloseAsync(id, request.Summary ?? string.Empty, cancellationToken), http, time, created: false);

    private static async Task<IResult> ReviewAsync(Guid id, IncidentReviewRequest request, HttpContext http, IncidentService incidents, TimeProvider time, CancellationToken cancellationToken) =>
        Answer(await incidents.ReviewAsync(id, request.WhatHappened ?? string.Empty, request.Why ?? string.Empty, request.WhatChanges ?? string.Empty, cancellationToken), http, time, created: false);

    private static IResult Answer(Result<IncidentRecord> result, HttpContext http, TimeProvider time, bool created) =>
        ApiResults.From(result, http, record => created
            ? TypedResults.Created($"{ApiRouting.Prefix}/incidents/{record.Incident.Id}", IncidentViews.Response(record, time.GetUtcNow()))
            : (IResult)TypedResults.Ok(IncidentViews.Response(record, time.GetUtcNow())));
}

/// <summary>An incident as the API answers it, with each deadline's state at the moment asked.</summary>
public static class IncidentViews
{
    public static IncidentResponse Response(IncidentRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var incident = record.Incident;
        var reviewState = incident.ReviewRecordedAt is not null ? "recorded"
            : incident.ReviewDueAt is null ? "not_due"
            : now >= incident.ReviewDueAt ? "overdue"
            : "due";
        var review = incident.ReviewRecordedAt is { } recorded
            ? new IncidentReviewResponse(recorded, incident.WhatHappened!, incident.Why!, incident.WhatChanges!, recorded < incident.ReviewDueAt)
            : null;
        var tenants = record.Tenants.Select(t => new IncidentTenantResponse(t.TenantSlug, t.PersonalData, t.KnownAt, t.NoticeDueAt, t.NotifiedAt, t.Notice, NoticeState(t, now), t.NoticeEscalatedAt)).ToList();
        var timeline = record.Entries.Select(e => new IncidentEntryResponse(e.Id, e.At, e.ByName, e.Kind, e.Text, e.TenantSlug)).ToList();
        return new IncidentResponse(incident.Id, incident.Severity, incident.Summary, incident.Lead, incident.Communications, incident.Scribe, incident.Status, incident.DeclaredAt, incident.ClosedAt, incident.CloseSummary, incident.ReviewDueAt, reviewState, review, tenants, timeline);
    }

    private static string NoticeState(IncidentTenant tenant, DateTimeOffset now) =>
        tenant.NoticeDueAt is not { } due ? "not_due"
        : tenant.NotifiedAt is { } notified ? (notified < due ? "notified" : "notified_late")
        : now >= due ? "overdue"
        : "due";
}
