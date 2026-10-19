using System.Text.Json;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Iron.Kernel.Settings.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Settings.Api;

/// <summary>A save: where (<c>tenant</c>, <c>unit</c> with its id, or <c>user</c> for oneself) and the value as JSON.</summary>
public sealed record SaveSettingRequest(string? Scope, Guid? UnitId, JsonElement Value);

/// <summary>The settings a place may hold, with the place they were read for.</summary>
public sealed record SettingsListResponse(string Scope, UnitReference? Unit, IReadOnlyList<SettingView> Data);

/// <summary>One change of a setting at a place: set or reset, the values before and after, who and when.</summary>
public sealed record SettingChangeResponse(Guid Id, string Kind, JsonElement? Before, JsonElement? After, Guid ChangedById, string? ChangedByName, DateTimeOffset ChangedAt);

/// <summary>
/// Settings in the unit (TF-057, TF-058, TF-221; ADR-0036). Reading needs <c>settings.values.read</c> at the place read;
/// changing a tenant's or a unit's value needs <c>settings.values.manage</c> there (tenant-wide for the tenant, at the unit
/// or above it for a unit, TF-221 AC3); a person changes their own values without a permission. So the reads take any
/// scope through the filter and check the place in the handler, and the writes are for anyone signed in and check the
/// place in the handler; the evaluator audits every denial.
/// </summary>
internal static class SettingsEndpoints
{
    public static readonly ListFields<SettingChange> HistoryFields = new ListFields<SettingChange>()
        .Add("id", c => c.Id, FieldUse.Select | FieldUse.Filter)
        .Add("kind", c => c.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("changed_by", c => c.CreatedBy, FieldUse.Select | FieldUse.Filter)
        .Add("changed_at", c => c.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("changed_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapApiGroup("settings");
        settings.MapGet("/", ListAsync).RequirePermission(SettingsPermissions.Read).ScopedByRow().WithName("ListSettings").WithSummary("Every setting a place may hold (scope=tenant, or scope=unit&unit=<id>), with its effective value there and where it comes from.");
        settings.MapGet("/{key}/history", HistoryAsync).RequirePermission(SettingsPermissions.Read).ScopedByRow().WithName("GetSettingHistory").WithSummary("Every change of a setting at a place, newest first, with who and when.");
        settings.MapPut("/{key}", SaveAsync).RequireSignedIn().WithName("SaveSetting").WithSummary("Set a value for the tenant, a unit, or yourself; settings.values.manage at that place for the first two.");
        settings.MapDelete("/{key}", ResetAsync).RequireSignedIn().WithName("ResetSetting").WithSummary("Remove the value at a place, so the inherited value applies; settings.values.manage at that place for a tenant or a unit.");
    }

    private static async Task<IResult> ListAsync(string? scope, Guid? unit, HttpContext http, SettingsConsole console, CancellationToken cancellationToken)
    {
        var place = Place(scope ?? "tenant", unit, http, allowUser: false);
        if (place.IsFailure)
        {
            return ApiResults.Problem(place.Error, http);
        }

        if (await RefusalAsync(http, SettingsPermissions.Read, place.Value, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var where = place.Value.Level == SettingLevel.Unit ? await console.UnitAsync(place.Value.TargetId!.Value, cancellationToken).ConfigureAwait(false) : null;
        if (place.Value.Level == SettingLevel.Unit && where is null)
        {
            return ApiResults.Problem(Error.NotFound(SettingsErrors.UnitUnknown, "There is no such organisation unit."), http);
        }

        var views = await console.ListAsync(place.Value, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SettingsListResponse(Level(place.Value.Level), where, views));
    }

    private static async Task<IResult> HistoryAsync(string key, string? scope, Guid? unit, HttpContext http, SettingsDbContext context, SettingsConsole console, SettingDeclarations declarations, CancellationToken cancellationToken)
    {
        if (declarations.Find(key) is null)
        {
            return ApiResults.Problem(Error.NotFound(SettingsErrors.Unknown, $"There is no setting '{key}'."), http);
        }

        var place = Place(scope ?? "tenant", unit, http, allowUser: false);
        if (place.IsFailure)
        {
            return ApiResults.Problem(place.Error, http);
        }

        if (await RefusalAsync(http, SettingsPermissions.Read, place.Value, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var parsed = ListQueryParser.Parse(http.Request.Query, HistoryFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var level = place.Value.Level;
        var target = place.Value.StoredTarget;
        var page = await context.History.AsNoTracking()
            .Where(c => c.Key == key && c.Level == level && c.TargetId == target)
            .ApplyListQuery(query, HistoryFields)
            .ToPageAsync(query.Page, cancellationToken)
            .ConfigureAwait(false);
        var names = await console.NamesAsync([.. page.Items.Select(c => c.CreatedBy).Distinct()], cancellationToken).ConfigureAwait(false);
        return ApiResults.List(page.Map(change => new SettingChangeResponse(change.Id, change.Kind, Json(change.Before), Json(change.After), change.CreatedBy, names.GetValueOrDefault(change.CreatedBy), change.CreatedAt)), http, query);
    }

    private static async Task<IResult> SaveAsync(string key, SaveSettingRequest request, HttpContext http, SettingsWriter writer, SettingsConsole console, SettingDeclarations declarations, CancellationToken cancellationToken)
    {
        var place = Place(request.Scope, request.UnitId, http, allowUser: true);
        if (place.IsFailure)
        {
            return ApiResults.Problem(place.Error, http);
        }

        if (await RefusalAsync(http, SettingsPermissions.Manage, place.Value, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var saved = await writer.SetAsync(key, place.Value, request.Value, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return ApiResults.Problem(saved.Error, http);
        }

        return TypedResults.Ok(await console.ViewAsync(declarations.Find(key)!, place.Value, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ResetAsync(string key, string? scope, Guid? unit, HttpContext http, SettingsWriter writer, SettingsConsole console, SettingDeclarations declarations, CancellationToken cancellationToken)
    {
        var place = Place(scope, unit, http, allowUser: true);
        if (place.IsFailure)
        {
            return ApiResults.Problem(place.Error, http);
        }

        if (await RefusalAsync(http, SettingsPermissions.Manage, place.Value, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reset = await writer.ResetAsync(key, place.Value, cancellationToken).ConfigureAwait(false);
        if (reset.IsFailure)
        {
            return ApiResults.Problem(reset.Error, http);
        }

        // The answer is the value that applies now: the inherited one (TF-058 AC2).
        return TypedResults.Ok(await console.ViewAsync(declarations.Find(key)!, place.Value, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The place a request names. A person's own place is always the signed-in principal: nobody sets another's.</summary>
    private static Result<SettingPlace> Place(string? scope, Guid? unit, HttpContext http, bool allowUser)
    {
        var words = allowUser ? "tenant, unit (with unit_id) or user" : "tenant, or unit with the unit's id";
        switch (scope?.Trim().ToLowerInvariant())
        {
            case "tenant":
                return SettingPlace.Tenant;
            case "unit" when unit is { } unitId && unitId != Guid.Empty:
                return SettingPlace.Unit(unitId);
            case "user" when allowUser:
                return SettingPlace.User(http.RequestServices.GetRequiredService<ICurrentPrincipal>().Id);
            default:
                return Error.Validation(SettingsErrors.ScopeInvalid, $"Say where the setting applies: the scope is {words}.", new FieldError("scope", "invalid", $"Use {words}."));
        }
    }

    /// <summary>
    /// The permission at the place, or the refusal. A person's own place needs none. The evaluator writes the denial's
    /// audit event (ADR-0031), so nothing else is written here.
    /// </summary>
    private static async Task<IResult?> RefusalAsync(HttpContext http, Permission permission, SettingPlace place, CancellationToken cancellationToken)
    {
        if (place.Level == SettingLevel.User)
        {
            return null;
        }

        var principal = http.RequestServices.GetRequiredService<ICurrentPrincipal>();
        var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
        var scope = place.Level == SettingLevel.Unit ? AccessScope.Unit(place.TargetId!.Value) : AccessScope.Tenant;
        var decision = await evaluator.EvaluateAsync(new AccessRequest(principal.Id, permission, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = scope }, cancellationToken).ConfigureAwait(false);
        if (decision.Allowed)
        {
            return null;
        }

        var where = place.Level == SettingLevel.Unit ? "at this unit or a unit above it" : "for the whole organisation";
        var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, $"You need {permission.Name} {where} for this. Ask an administrator who holds it."), http);
        problem.Extensions["permission"] = permission.Name;
        return TypedResults.Problem(problem);
    }

    private static string Level(SettingLevel level) => level.ToString().ToLowerInvariant();

    private static JsonElement? Json(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
