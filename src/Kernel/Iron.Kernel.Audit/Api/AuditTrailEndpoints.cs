using System.Net.Http.Json;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Files.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Audit.Api;

public sealed record AuditExportResponse(Guid Id, int Rows, DateTimeOffset CreatedAt, string DownloadUrl);

public sealed record ArchivedEventsResponse(IReadOnlyList<AuditEventResponse> Data, int BatchesRead, bool Truncated);

public sealed record ChainSealResponse(long FirstPosition, long LastPosition, string LastHash, DateTimeOffset SealedAt);

public sealed record ChainVerificationResponse(
    string Result,
    bool Full,
    long FromPosition,
    long ToPosition,
    int EventsChecked,
    IReadOnlyList<ChainVerificationProblem> Problems,
    long? RestorePosition,
    DateTimeOffset? RestoreAt,
    DateTimeOffset VerifiedAt);

public sealed record ChainVerificationProblem(string Kind, long Position, Guid? EventId, string Message);

/// <summary>Where the tenant's chain stands (TF-061): its head, what waits for a seal, the archive and pruning, the latest seals, and the control plane's view.</summary>
public sealed record AuditChainResponse(
    long? HeadPosition,
    string? HeadHash,
    DateTimeOffset? HeadSealedAt,
    int Unsealed,
    long ArchivedUpTo,
    long PrunedUpTo,
    IReadOnlyList<ChainSealResponse> Seals,
    bool Anchored,
    long? AnchoredUpTo,
    ChainVerificationResponse? LatestVerification,
    int Segment,
    IReadOnlyList<ChainRestoreResponse> Restores);

/// <summary>
/// A restore an operator accepted (ADR-0050): the chain continues as <paramref name="Segment"/> from the position after
/// <paramref name="RestorePosition"/>; the positions from <paramref name="LostFrom"/> to <paramref name="LostTo"/> were lost.
/// </summary>
public sealed record ChainRestoreResponse(
    int Segment,
    long StartPosition,
    long RestorePosition,
    DateTimeOffset RestoreAt,
    long LostFrom,
    long LostTo,
    string Ticket,
    string Reason,
    string AcceptedBy,
    DateTimeOffset AcceptedAt);

public sealed record RetentionClassResponse(string Code, string Description, int DefaultDays, int MinimumDays, int MaximumDays, int Days, bool Chosen, IReadOnlyList<string> RecordTypes)
{
    /// <summary>The tenant's choice, whose field-level history the page shows (TF-062); null while the class keeps its default.</summary>
    public Guid? ChoiceId { get; init; }
}

public sealed record RetentionClassesResponse(IReadOnlyList<RetentionClassResponse> Classes);

public sealed record RetentionChoiceRequest(int Days, string? Reason);

public sealed record LegalHoldResponse(Guid Id, string Scope, string? RecordType, Guid? RecordId, string Reason, DateTimeOffset PlacedAt, Guid PlacedBy, DateTimeOffset? ReleasedAt, string? ReleaseReason)
{
    /// <summary>The name of who placed it, when the directory knows them.</summary>
    public string? PlacedByName { get; init; }

    public static LegalHoldResponse From(LegalHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return new(hold.Id, hold.Scope, hold.RecordType, hold.RecordId, hold.Reason, hold.CreatedAt, hold.CreatedBy, hold.ReleasedAt, hold.ReleaseReason);
    }
}

public sealed record LegalHoldsResponse(IReadOnlyList<LegalHoldResponse> Holds);

public sealed record PlaceLegalHoldRequest(string Scope, string? RecordType, Guid? RecordId, string Reason);

public sealed record ReleaseLegalHoldRequest(string Reason);

/// <summary>
/// The audit trail beyond its list (ADR-0050): the export to CSV and its download (TF-064), the archive's search by date
/// range (TF-064 AC2), the chain's state (TF-061), the retention classes (TF-066 AC3) and the legal holds (TF-066 AC2).
/// Reading needs <c>audit.events.read</c>; choosing a retention and placing or releasing a hold need their own
/// permission and a recent confirmation that it is the person (TF-248).
/// </summary>
internal static class AuditTrailEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapApiGroup("audit-events");
        events.MapPost("/exports", ExportAsync).RequirePermission(AuditPermissions.EventsRead).RateLimited(RateLimitPolicies.Exports).Produces<AuditExportResponse>(StatusCodes.Status201Created).WithName("ExportAuditEvents").WithSummary("Export a search of the audit trail to CSV, with its filters in the first lines; the export is itself audited.");
        events.MapGet("/exports/{id:guid}/content", DownloadAsync).RequirePermission(AuditPermissions.EventsRead).Produces<string>(StatusCodes.Status200OK, "text/csv").WithName("DownloadAuditExport").WithSummary("Download an export of the audit trail.");
        events.MapGet("/archived", ArchivedAsync).RequirePermission(AuditPermissions.EventsRead).Produces<ArchivedEventsResponse>(StatusCodes.Status200OK).WithName("SearchArchivedAuditEvents").WithSummary("Search the archived audit trail by date range, actor, record, type and source, oldest first.");

        var chain = endpoints.MapApiGroup("audit-chain");
        chain.MapGet("/", ChainAsync).RequirePermission(AuditPermissions.EventsRead).Produces<AuditChainResponse>(StatusCodes.Status200OK).WithName("GetAuditChain").WithSummary("Where the audit chain stands: its head, its archive and pruning, its seals, and the control plane's latest verification.");

        var retention = endpoints.MapApiGroup("retention");
        retention.MapGet("/", RetentionAsync).RequirePermission(AuditPermissions.EventsRead).Produces<RetentionClassesResponse>(StatusCodes.Status200OK).WithName("ListRetentionClasses").WithSummary("How long each kind of record is kept, within the operator's limits, and which record types are in each.");
        retention.MapPut("/{code}", ChooseAsync).RequirePermission(AuditPermissions.RetentionManage).RequireRecentAuthentication().Produces<RetentionClassResponse>(StatusCodes.Status200OK).WithName("ChooseRetention").WithSummary("Choose how long a class keeps its records; below the operator's minimum is refused.");

        var holds = endpoints.MapApiGroup("legal-holds");
        holds.MapGet("/", HoldsAsync).RequirePermission(AuditPermissions.EventsRead).Produces<LegalHoldsResponse>(StatusCodes.Status200OK).WithName("ListLegalHolds").WithSummary("Every legal hold, standing and released, newest first.");
        holds.MapPost("/", PlaceAsync).RequirePermission(AuditPermissions.LegalHoldsManage).RequireRecentAuthentication().Produces<LegalHoldResponse>(StatusCodes.Status201Created).WithName("PlaceLegalHold").WithSummary("Place a legal hold on the whole tenant, a record, or every record of a master, with a reason.");
        holds.MapPost("/{id:guid}/release", ReleaseAsync).RequirePermission(AuditPermissions.LegalHoldsManage).RequireRecentAuthentication().Produces<LegalHoldResponse>(StatusCodes.Status200OK).WithName("ReleaseLegalHold").WithSummary("Release a legal hold, with a reason; it stays as a record.");
    }

    private static async Task<IResult> ExportAsync(HttpContext http, AuditDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, AuditEventsEndpoints.Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        if (http.RequestServices.GetService<AuditExporter>() is not { } exporter || http.RequestServices.GetService<IObjectStorage>() is null or NoObjectStorage)
        {
            return ApiResults.Problem(Error.Unavailable("audit.export.no_storage", "This host keeps no files, so it cannot write an export."), http);
        }

        var query = parsed.Value;
        var search = context.Events.AsNoTracking().ApplyListQuery(query, AuditEventsEndpoints.Fields);
        // The filters as the search was written, in the list grammar, so the file says exactly what it holds.
        var filters = http.Request.Query["filter"].Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToList();
        var names = await NamesAsync(http, search, cancellationToken);
        var export = await exporter.ExportAsync(search, filters, names, cancellationToken);
        var url = $"{ApiRouting.Prefix}/audit-events/exports/{export.Id}/content";
        return TypedResults.Created(url, new AuditExportResponse(export.Id, export.Rows, export.CreatedAt, url));
    }

    private static async Task<IResult> DownloadAsync(Guid id, HttpContext http, AuditDbContext context, CancellationToken cancellationToken)
    {
        var export = await context.Exports.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        var stored = export is null || http.RequestServices.GetService<IObjectStorage>() is not { } storage || storage is NoObjectStorage ? null : await storage.GetAsync(export.BlobKey, cancellationToken);
        if (stored is null)
        {
            return ApiResults.Problem(Error.NotFound(AuditErrors.ExportNotFound, "There is no such export."), http);
        }

        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Stream(stored.Content, "text/csv; charset=utf-8", $"audit-trail-{export!.CreatedAt:yyyyMMdd-HHmmss}.csv");
    }

    private static async Task<IResult> ArchivedAsync(
        HttpContext http,
        AuditArchiveSearch search,
        DateTimeOffset? from,
        DateTimeOffset? to,
        [FromQuery(Name = "event_type")] string? eventType,
        [FromQuery(Name = "actor_id")] Guid? actorId,
        [FromQuery(Name = "impersonator_id")] Guid? impersonatorId,
        [FromQuery(Name = "record_type")] string? recordType,
        [FromQuery(Name = "record_id")] Guid? recordId,
        string? source,
        CancellationToken cancellationToken)
    {
        if (http.RequestServices.GetService<IObjectStorage>() is null or NoObjectStorage)
        {
            return ApiResults.Problem(Error.Unavailable("audit.archive.no_storage", "This host keeps no archive: it is in each tenant's unit."), http);
        }

        if (from is null || to is null || to < from)
        {
            return ApiResults.Problem(Error.Validation(AuditErrors.ArchiveRangeRequired, "Give the archive's search a date range, from and to.", new FieldError("from", "required", "Choose the first day.")), http);
        }

        var (events, batches, truncated) = await search.SearchAsync(new ArchiveQuery(from.Value, to.Value, eventType, actorId, impersonatorId, recordType, recordId, source), cancellationToken);
        var ids = events.Select(e => e.Content.ActorId).Concat(events.Where(e => e.Content.ImpersonatorId is not null).Select(e => e.Content.ImpersonatorId!.Value)).Distinct().ToList();
        var names = http.RequestServices.GetService<IPrincipals>() is { } principals
            ? (await principals.FindAsync(ids, cancellationToken)).ToDictionary(p => p.Key, p => p.Value.DisplayName)
            : new Dictionary<Guid, string>();
        var eventNames = http.RequestServices.GetService<AuditEventNames>();
        return TypedResults.Ok(new ArchivedEventsResponse([.. events.Select(e => AuditEventResponse.FromArchive(e, names) with { EventName = eventNames?.NameOf(e.Content.EventType) ?? e.Content.EventType })], batches, truncated));
    }

    private static async Task<IResult> ChainAsync(AuditDbContext context, ControlPlaneClient controlPlane, CancellationToken cancellationToken)
    {
        var head = await AuditSealer.HeadAsync(context, cancellationToken);
        var unsealed = await context.Events.CountAsync(e => e.ChainPosition == null, cancellationToken);
        var archivedUpTo = await context.ArchiveBatches.MaxAsync(b => (long?)b.LastPosition, cancellationToken) ?? 0;
        var prunedUpTo = await context.Prunes.MaxAsync(p => (long?)p.UpToPosition, cancellationToken) ?? 0;
        var seals = await context.Seals.AsNoTracking().OrderByDescending(s => s.LastPosition).Take(10)
            .Select(s => new ChainSealResponse(s.FirstPosition, s.LastPosition, s.LastHash, s.CreatedAt)).ToListAsync(cancellationToken);

        // Every restore stays on the page for good, with what it lost (ADR-0050), newest first.
        var restores = await context.ChainRestores.AsNoTracking().OrderByDescending(r => r.Segment)
            .Select(r => new ChainRestoreResponse(r.Segment, r.StartPosition, r.RestorePosition, r.RestoreAt, r.LostFrom, r.LostTo, r.Ticket, r.Reason, r.AcceptedBy, r.AcceptedAt))
            .ToListAsync(cancellationToken);

        long? anchoredUpTo = null;
        ChainVerificationResponse? verification = null;
        var anchored = false;
        if (controlPlane.IsConfigured)
        {
            try
            {
                using var anchors = await controlPlane.SendAsync(HttpMethod.Get, "/api/v1/audit-anchors/latest", null, cancellationToken);
                if (anchors.IsSuccessStatusCode)
                {
                    anchored = true;
                    anchoredUpTo = (await anchors.Content.ReadFromJsonAsync<LatestAnchors>(ControlPlaneClient.Json, cancellationToken))?.Seal?.Position;
                }

                using var latest = await controlPlane.SendAsync(HttpMethod.Get, "/api/v1/audit-verifications/latest", null, cancellationToken);
                if (latest.IsSuccessStatusCode)
                {
                    verification = (await latest.Content.ReadFromJsonAsync<LatestVerification>(ControlPlaneClient.Json, cancellationToken))?.Verification;
                }
            }
            catch (HttpRequestException)
            {
                anchored = false; // the page says the control plane could not be asked
            }
        }

        return TypedResults.Ok(new AuditChainResponse(head?.Position, head?.Hash, head?.SealedAt, unsealed, archivedUpTo, prunedUpTo, seals, anchored, anchoredUpTo, verification, restores.Count == 0 ? 1 : restores[0].Segment, restores));
    }

    private static async Task<IResult> RetentionAsync(RetentionPolicy policy, IEnumerable<IRetainedRecords> types, CancellationToken cancellationToken)
    {
        var declared = types.ToList();
        var classes = await policy.ClassesAsync(cancellationToken);
        return TypedResults.Ok(new RetentionClassesResponse([.. classes.Select(c => Response(c, declared))]));
    }

    private static async Task<IResult> ChooseAsync(string code, RetentionChoiceRequest request, HttpContext http, RetentionPolicy policy, IEnumerable<IRetainedRecords> types, CancellationToken cancellationToken)
    {
        var result = await policy.ChooseAsync(code, request.Days, request.Reason, cancellationToken);
        return ApiResults.From(result, http, view => TypedResults.Ok(Response(view, [.. types])));
    }

    private static async Task<IResult> HoldsAsync(HttpContext http, AuditDbContext context, CancellationToken cancellationToken)
    {
        var holds = await context.LegalHolds.AsNoTracking().OrderByDescending(h => h.CreatedAt).ToListAsync(cancellationToken);
        var names = http.RequestServices.GetService<IPrincipals>() is { } principals
            ? (await principals.FindAsync([.. holds.Select(h => h.CreatedBy).Distinct()], cancellationToken)).ToDictionary(p => p.Key, p => p.Value.DisplayName)
            : new Dictionary<Guid, string>();
        return TypedResults.Ok(new LegalHoldsResponse([.. holds.Select(h => LegalHoldResponse.From(h) with { PlacedByName = names.GetValueOrDefault(h.CreatedBy) })]));
    }

    private static async Task<IResult> PlaceAsync(PlaceLegalHoldRequest request, HttpContext http, LegalHolds holds, CancellationToken cancellationToken)
    {
        var result = await holds.PlaceAsync(request.Scope, request.RecordType, request.RecordId, request.Reason, cancellationToken);
        return ApiResults.From(result, http, hold => TypedResults.Created($"{ApiRouting.Prefix}/legal-holds/{hold.Id}", LegalHoldResponse.From(hold)));
    }

    private static async Task<IResult> ReleaseAsync(Guid id, ReleaseLegalHoldRequest request, HttpContext http, LegalHolds holds, CancellationToken cancellationToken)
    {
        var result = await holds.ReleaseAsync(id, request.Reason, cancellationToken);
        return ApiResults.From(result, http, hold => TypedResults.Ok(LegalHoldResponse.From(hold)));
    }

    private static RetentionClassResponse Response(RetentionClassView view, IReadOnlyList<IRetainedRecords> types) =>
        new(view.Class.Code, view.Class.Description, view.Class.DefaultDays, view.MinimumDays, view.MaximumDays, view.Days, view.Chosen,
            [.. types.Where(t => t.Class.Code == view.Class.Code).Select(t => t.RecordType).Order(StringComparer.Ordinal)])
        {
            ChoiceId = view.ChoiceId,
        };

    private static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(HttpContext http, IQueryable<AuditEventRecord> search, CancellationToken cancellationToken)
    {
        if (http.RequestServices.GetService<IPrincipals>() is not { } principals)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = await search.Select(e => e.ActorId).Distinct().Take(10_000).ToListAsync(cancellationToken);
        var impersonators = await search.Where(e => e.ImpersonatorId != null).Select(e => e.ImpersonatorId!.Value).Distinct().Take(1_000).ToListAsync(cancellationToken);
        return (await principals.FindAsync([.. ids.Concat(impersonators).Distinct()], cancellationToken)).ToDictionary(p => p.Key, p => p.Value.DisplayName);
    }

    private sealed record LatestAnchor(long Position, string Hash, DateTimeOffset At);

    private sealed record LatestAnchors(LatestAnchor? Seal, LatestAnchor? Pruned);

    private sealed record LatestVerification(ChainVerificationResponse? Verification);
}
