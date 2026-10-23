using System.Text.Json;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Changes.Data;
using Iron.Kernel.Changes.Services;
using Iron.Kernel.Compliance.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Changes.Api;

/// <summary>The credentials of a signature, as the signing dialog sends them, when the tenant asks for one in the change's area.</summary>
public sealed record ChangeSignatureRequest(string? LoginId, string? Password, string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public SigningCredentials ToCredentials() => new(LoginId, Password, Code, RecoveryCode, Passkey);
}

public sealed record DecideChangeRequest(string? Reason, ChangeSignatureRequest? Signature);

/// <summary>
/// A change of sensitive configuration (TF-068): what it changes, its state before and after side by side, who asked and
/// why, where it stands, who decided it, and whether the person looking may decide it and must sign to.
/// </summary>
public sealed record PendingChangeResponse(
    Guid Id,
    string Area,
    string Kind,
    string Subject,
    string Title,
    JsonElement Before,
    JsonElement After,
    string? Reason,
    Guid RequestedBy,
    string RequesterName,
    DateTimeOffset RequestedAt,
    string Status,
    DateTimeOffset ExpiresAt,
    int Reviews,
    DateTimeOffset? RestartedAt,
    string? RestartNote,
    Guid? DecidedBy,
    string? DeciderName,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    Guid? SignatureId,
    string Permission,
    Guid? ScopeUnitId,
    bool MayDecide,
    bool Signed)
{
    public static PendingChangeResponse From(ChangeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var c = item.Change;
        return new(c.Id, c.Area, c.Kind, c.Subject, c.Title, c.Before, c.After, c.Reason, c.RequestedById, c.RequesterName, c.CreatedAt, c.Status, c.ExpiresAt, c.Reviews, c.RestartedAt, c.RestartNote, c.DecidedById, c.DeciderName, c.DecidedAt, c.DecisionReason, c.SignatureId, c.Permission, c.ScopeUnitId, item.MayDecide, item.Signed);
    }
}

/// <summary>
/// The pending changes of sensitive configuration (TF-068, ADR-0052). A person lists their own and the ones they may
/// decide; <c>changes.changes.read</c> lists every one. Deciding needs the change's own permission at its place, checked
/// in the service with a confirmation that it is the person or their signature; the requester only withdraws.
/// </summary>
internal static class ChangesEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var changes = endpoints.MapApiGroup("changes");
        changes.MapGet("/", ListAsync).RequireSignedIn().Produces<ListResponse<PendingChangeResponse>>(StatusCodes.Status200OK).WithName("ListConfigurationChanges").WithSummary("Changes of sensitive configuration, newest first: yours (view=mine), the ones you may decide (view=to_approve), or every one (view=all, with changes.changes.read).");
        changes.MapGet("/{id:guid}", GetAsync).RequireSignedIn().Produces<PendingChangeResponse>(StatusCodes.Status200OK).WithName("GetConfigurationChange").WithSummary("One change with its before and after.");
        changes.MapPost("/{id:guid}/approve", ApproveAsync).RequireSignedIn().Produces<PendingChangeResponse>(StatusCodes.Status200OK).WithName("ApproveConfigurationChange").WithSummary("Approve a change someone else asked for: it applies at once, unless what it changes changed meanwhile.");
        changes.MapPost("/{id:guid}/reject", RejectAsync).RequireSignedIn().Produces<PendingChangeResponse>(StatusCodes.Status200OK).WithName("RejectConfigurationChange").WithSummary("Reject a change someone else asked for, with a reason; nothing applies.");
        changes.MapPost("/{id:guid}/withdraw", WithdrawAsync).RequireSignedIn().Produces<PendingChangeResponse>(StatusCodes.Status200OK).WithName("WithdrawConfigurationChange").WithSummary("Take back a change you asked for, before anyone decides it.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, PendingChanges changes, IPermissionEvaluator evaluator, ICurrentPrincipal current, string? view, string? status, [FromQuery(Name = "page_number")] int? pageNumber, [FromQuery(Name = "page_size")] int? pageSize, CancellationToken cancellationToken)
    {
        var chosen = view switch
        {
            "to_approve" => ChangeView.ToApprove,
            "all" => ChangeView.All,
            null or "" or "mine" => ChangeView.Mine,
            _ => (ChangeView?)null,
        };
        if (chosen is null)
        {
            return ApiResults.Problem(Error.Validation("changes.view.unknown", "Choose the view mine, to_approve or all."), http);
        }

        if (status is not null && !PendingChangeStates.All.Contains(status, StringComparer.Ordinal))
        {
            return ApiResults.Problem(Error.Validation("changes.status.unknown", $"Choose a status: {string.Join(", ", PendingChangeStates.All)}."), http);
        }

        if (chosen == ChangeView.All && !(await evaluator.EvaluateAsync(new AccessRequest(current.Id, ChangesPermissions.Read, "read", http.Request.Path), cancellationToken)).Allowed)
        {
            return ApiResults.Problem(Error.Forbidden(PermissionErrors.PermissionDenied, "Every change is shown with changes.changes.read; you see your own and the ones you may decide."), http);
        }

        var items = await changes.ListAsync(chosen.Value, status, cancellationToken);
        var request = PageRequest.From(pageNumber, pageSize);
        var page = new Page<PendingChangeResponse>([.. items.Skip(request.Skip).Take(request.PageSize).Select(PendingChangeResponse.From)], request, items.Count);
        return ApiResults.List(page, http);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, PendingChanges changes, CancellationToken cancellationToken)
    {
        var found = await changes.FindAsync(id, cancellationToken);
        return found is null
            ? ApiResults.Problem(Error.NotFound(ChangesErrors.NotFound, "There is no such change, or it is not yours to see."), http)
            : TypedResults.Ok(PendingChangeResponse.From(found));
    }

    private static async Task<IResult> ApproveAsync(Guid id, DecideChangeRequest? request, HttpContext http, PendingChanges changes, CancellationToken cancellationToken)
    {
        var decided = await changes.ApproveAsync(id, request?.Reason, request?.Signature?.ToCredentials(), cancellationToken);
        return ApiResults.From(decided, http, item => TypedResults.Ok(PendingChangeResponse.From(item)));
    }

    private static async Task<IResult> RejectAsync(Guid id, DecideChangeRequest? request, HttpContext http, PendingChanges changes, CancellationToken cancellationToken)
    {
        var decided = await changes.RejectAsync(id, request?.Reason, request?.Signature?.ToCredentials(), cancellationToken);
        return ApiResults.From(decided, http, item => TypedResults.Ok(PendingChangeResponse.From(item)));
    }

    private static async Task<IResult> WithdrawAsync(Guid id, HttpContext http, PendingChanges changes, CancellationToken cancellationToken)
    {
        var withdrawn = await changes.WithdrawAsync(id, cancellationToken);
        return ApiResults.From(withdrawn, http, item => TypedResults.Ok(PendingChangeResponse.From(item)));
    }
}
