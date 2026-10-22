using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Identity.Api;

/// <summary>
/// A session as the lists show it (TF-022): whose, the browser and platform, the city and country, when it started and
/// was last used, how it signed in, who is signed in as the person when someone is, and whether it is the asker's own.
/// </summary>
public sealed record SessionRecordResponse(
    Guid Id,
    Guid PrincipalId,
    string LoginId,
    string DisplayName,
    string? Browser,
    string? Platform,
    string? City,
    string? Country,
    string Method,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    string? ImpersonatorName,
    bool Current)
{
    public static SessionRecordResponse From(SessionRow row, Guid? currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SessionRecordResponse(row.Id, row.PrincipalId, row.LoginId, row.DisplayName, row.Browser, row.Platform, row.City, row.Country, row.Method, row.StartedAt, row.LastActivityAt, row.ImpersonatorName, row.Id == currentSessionId);
    }
}

public sealed record SessionDayResponse(DateOnly Date, int Started, int PeakPeople, int EndedByAdministrator);

/// <summary>The sessions console's summary (TF-022): people signed in now, and each of the last days.</summary>
public sealed record SessionSummaryResponse(int SignedInNow, IReadOnlyList<SessionDayResponse> Days);

/// <summary>How many sessions a request ended.</summary>
public sealed record SessionsEndedResponse(int SessionsEnded);

/// <summary>Why an administrator ends a session: required, kept in the audit trail and the person's security activity.</summary>
public sealed record EndSessionRequest(string Reason);

public sealed class EndSessionRequestValidator : AbstractValidator<EndSessionRequest>
{
    public EndSessionRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the person and the auditors will read.").MaximumLength(SecurityEvent.ReasonMaxLength);
    }
}

/// <summary>Why an administrator revokes everything of a principal.</summary>
public sealed record RevokeRequest(string Reason);

public sealed class RevokeRequestValidator : AbstractValidator<RevokeRequest>
{
    public RevokeRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the person and the auditors will read.").MaximumLength(SecurityEvent.ReasonMaxLength);
    }
}

/// <summary>Why the whole tenant's sessions and tokens are revoked, and the tenant's identifier typed to confirm it.</summary>
public sealed record RevokeTenantRequest(string Reason, string Confirmation);

public sealed class RevokeTenantRequestValidator : AbstractValidator<RevokeTenantRequest>
{
    public RevokeTenantRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the auditors will read.").MaximumLength(TenantRevocation.ReasonMaxLength);
        RuleFor(r => r.Confirmation).NotEmpty().WithMessage("Type the tenant's identifier to confirm.").MaximumLength(63);
    }
}

/// <summary>
/// Sessions (TF-021, TF-022, TF-029, ADR-0049). A person lists their own and ends any of the others, or all of them, from
/// My profile. An administrator with <c>identity.sessions.manage</c> lists everyone's live sessions with the summary, ends
/// any with a reason, and revokes every session and token of the tenant with a reason and the tenant's identifier typed;
/// both changes ask for a recent authentication (TF-248).
/// </summary>
internal static class SessionEndpoints
{
    public static readonly ListFields<SessionRow> Fields = new ListFields<SessionRow>()
        .Add("id", s => s.Id, FieldUse.Select | FieldUse.Filter)
        .Add("principal_id", s => s.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("login_id", s => s.LoginId)
        .Add("display_name", s => s.DisplayName)
        .Add("browser", s => s.Browser, FieldUse.Select | FieldUse.Filter)
        .Add("platform", s => s.Platform, FieldUse.Select | FieldUse.Filter)
        .Add("city", s => s.City, FieldUse.Select | FieldUse.Filter)
        .Add("country", s => s.Country, FieldUse.Select | FieldUse.Filter)
        .Add("method", s => s.Method, FieldUse.Select | FieldUse.Filter)
        .Add("started_at", s => s.StartedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("last_activity_at", s => s.LastActivityAt, FieldUse.Select | FieldUse.Sort)
        .Add("impersonator_name", s => s.ImpersonatorName, FieldUse.Select)
        .SortByDefault("last_activity_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapGroup("/api/v1/me/sessions").WithTags("me");
        me.MapGet("/", MineAsync).RequireSignedIn().Produces<ListResponse<SessionRecordResponse>>(StatusCodes.Status200OK).WithName("MySessions").WithSummary("Your live sessions: the browser, the city and country, when each started and was last used, and which is this one.");
        me.MapPost("/{id:guid}/end", EndMineAsync).RequireSignedIn().ForbiddenDuringImpersonation().Produces(StatusCodes.Status204NoContent).WithName("EndMySession").WithSummary("End one of your other sessions; sign out to end this one.");
        me.MapPost("/end-others", EndOthersAsync).RequireSignedIn().ForbiddenDuringImpersonation().Produces<SessionsEndedResponse>(StatusCodes.Status200OK).WithName("EndMyOtherSessions").WithSummary("Sign out everywhere else: every session of yours but this one ends.");

        var sessions = endpoints.MapApiGroup("sessions");
        sessions.MapGet("/", ListAsync).RequirePermission(IdentityPermissions.SessionsManage).Produces<ListResponse<SessionRecordResponse>>(StatusCodes.Status200OK).WithName("ListSessions").WithSummary("Everyone's live sessions.");
        sessions.MapGet("/summary", SummaryAsync).RequirePermission(IdentityPermissions.SessionsManage).Produces<SessionSummaryResponse>(StatusCodes.Status200OK).WithName("SessionSummary").WithSummary("How many people are signed in now, and for each of the last days (?days=, 14 by default, 31 at most): sessions started, the peak of people signed in at once, and sessions an administrator ended.");
        sessions.MapPost("/{id:guid}/end", EndAsync).RequirePermission(IdentityPermissions.SessionsManage).RequireRecentAuthentication().Validate<EndSessionRequest>().Produces(StatusCodes.Status204NoContent).WithName("EndSession").WithSummary("End anyone's session, with a reason the person and the audit trail keep.");
        sessions.MapPost("/revoke-tenant", RevokeTenantAsync).RequirePermission(IdentityPermissions.SessionsManage).RequireRecentAuthentication().Validate<RevokeTenantRequest>().Produces<SessionsEndedResponse>(StatusCodes.Status200OK).WithName("RevokeTenant").WithSummary("Revoke every session and token of the tenant, yours included, with a reason and the tenant's identifier typed to confirm.");
    }

    private static async Task<IResult> MineAsync(HttpContext http, RequestPrincipal principal, SessionConsole console, SessionService sessions, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var current = principal.Current!;
        await sessions.EndExpiredAsync(cancellationToken);
        var query = parsed.Value;

        // Sessions in which someone else is signed in as the person are not the person's to end; their security activity says so.
        var mine = console.Live().Where(s => s.PrincipalId == current.Id && s.Method != SessionMethods.Impersonation);
        var page = await mine.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(row => SessionRecordResponse.From(row, current.Kind == AuthenticationKind.Session ? current.SessionId : null)), http, query);
    }

    private static async Task<IResult> EndMineAsync(Guid id, HttpContext http, RequestPrincipal principal, SessionConsole console, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        var result = await console.EndOwnAsync(current.Id, current.SessionId, id, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static async Task<IResult> EndOthersAsync(RequestPrincipal principal, SessionConsole console, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        return TypedResults.Ok(new SessionsEndedResponse(await console.EndOthersAsync(current.Id, current.SessionId, cancellationToken)));
    }

    private static async Task<IResult> ListAsync(HttpContext http, RequestPrincipal principal, SessionConsole console, SessionService sessions, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        await sessions.EndExpiredAsync(cancellationToken);
        var query = parsed.Value;
        var page = await console.Live().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var current = principal.Current;
        return ApiResults.List(page.Map(row => SessionRecordResponse.From(row, current?.Kind == AuthenticationKind.Session ? current.SessionId : null)), http, query);
    }

    private static async Task<IResult> SummaryAsync(int? days, SessionConsole console, CancellationToken cancellationToken)
    {
        var summary = await console.SummaryAsync(days ?? 14, cancellationToken);
        return TypedResults.Ok(new SessionSummaryResponse(summary.SignedInNow, [.. summary.Days.Select(d => new SessionDayResponse(d.Date, d.Started, d.PeakPeople, d.EndedByAdministrator))]));
    }

    private static async Task<IResult> EndAsync(Guid id, EndSessionRequest request, HttpContext http, SessionConsole console, CancellationToken cancellationToken)
    {
        var result = await console.EndAsync(id, request.Reason, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static async Task<IResult> RevokeTenantAsync(RevokeTenantRequest request, HttpContext http, SessionConsole console, CancellationToken cancellationToken)
    {
        var result = await console.RevokeTenantAsync(request.Reason, request.Confirmation, cancellationToken);
        return ApiResults.From(result, http, ended => TypedResults.Ok(new SessionsEndedResponse(ended)));
    }
}
