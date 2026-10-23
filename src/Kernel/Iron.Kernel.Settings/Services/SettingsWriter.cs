using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Settings.Services;

/// <summary>Where a value is set: the tenant, a unit, or a person. The target is the unit or the person.</summary>
public sealed record SettingPlace(SettingLevel Level, Guid? TargetId)
{
    public static SettingPlace Tenant { get; } = new(SettingLevel.Tenant, null);

    public static SettingPlace Unit(Guid unitId) => new(SettingLevel.Unit, unitId);

    public static SettingPlace User(Guid principalId) => new(SettingLevel.User, principalId);

    public Guid StoredTarget => Level == SettingLevel.Tenant ? Guid.Empty : TargetId ?? Guid.Empty;
}

/// <summary>
/// Saving and resetting values (TF-057 AC2, TF-058, ADR-0036). A key no module declares, a place the setting does not
/// allow, and a value that breaks the rule are refused, the last with the rule in words on the field. Each change writes
/// the value, an append-only history row and the audit event in one transaction (ADR-0013), then refreshes this
/// process's cache so the next request sees it. Who may change a place is the endpoint's check.
/// </summary>
public sealed class SettingsWriter(
    SettingsDbContext context,
    IUnitOfWork unitOfWork,
    SettingDeclarations declarations,
    ICurrentPrincipal current,
    IAuditWriter audit,
    SettingsRefresher refresher,
    IOrganisationUnits? units = null,
    SecretStoreOptions? store = null,
    IRecentAuthentication? recent = null,
    IFourEyes? fourEyes = null)
{
    /// <summary>The kinds of change a governed setting's save becomes while it waits for a second administrator (ADR-0052).</summary>
    public const string SetKind = "settings.value.set";

    public const string ResetKind = "settings.value.reset";

    private static readonly JsonSerializerOptions Compact = new();

    /// <summary>A setting whose change is a high-risk action, such as the MFA policy, needs a recent authentication (TF-248).</summary>
    private Error? RecentEnough(SettingDefinition setting) =>
        setting.NeedsRecentAuthentication && recent?.Require() is { IsFailure: true } refused ? refused.Error : null;

    /// <summary>The setting, if this place may hold a value of it: the checks a save and a reset share.</summary>
    public async Task<Result<DeclaredSetting>> CheckAsync(string key, SettingPlace place, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (declarations.Find(key) is not { } declared)
        {
            return Error.NotFound(SettingsErrors.Unknown, $"There is no setting '{key}'.");
        }

        if (!declared.Definition.Allows(place.Level) || place.Level == SettingLevel.Default)
        {
            return Error.Validation(SettingsErrors.ScopeNotAllowed, $"The setting '{key}' cannot be set {Where(place.Level)}. It may be set {Allowed(declared.Definition)}.", new FieldError("scope", "not_allowed", $"It may be set {Allowed(declared.Definition)}."));
        }

        if (place.Level == SettingLevel.Unit && (place.TargetId is not { } unit || units is null || await units.FindAsync(unit, cancellationToken).ConfigureAwait(false) is null))
        {
            return Error.NotFound(SettingsErrors.UnitUnknown, "There is no such organisation unit.");
        }

        return declared;
    }

    public async Task<Result> SetAsync(string key, SettingPlace place, JsonElement value, CancellationToken cancellationToken)
    {
        var checkedSetting = await CheckAsync(key, place, cancellationToken).ConfigureAwait(false);
        if (checkedSetting.IsFailure)
        {
            return checkedSetting.Error;
        }

        var setting = checkedSetting.Value.Definition;
        if (setting.Problem(value) is { } problem)
        {
            return Error.Validation(SettingsErrors.ValueInvalid, $"The value for '{key}' is refused. {problem}", new FieldError("value", "invalid", problem));
        }

        if (RecentEnough(setting) is { } stale)
        {
            return stale;
        }

        if (setting.IsSecret && store?.Path is { Length: > 0 } unitPath && !value.GetString()!.StartsWith($"{unitPath.Trim('/')}/settings/", StringComparison.Ordinal))
        {
            var words = $"A path under {unitPath.Trim('/')}/settings/, the part of the store this unit reads.";
            return Error.Validation(SettingsErrors.ValueInvalid, $"The value for '{key}' is refused. {words}", new FieldError("value", "invalid", words));
        }

        var json = JsonSerializer.Serialize(value, Compact);
        if (await HeldAsync(setting, place, json, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(key, cancellationToken).ConfigureAwait(false);
        var row = await FindAsync(key, place, cancellationToken).ConfigureAwait(false);
        var before = row?.ValueJson;
        if (row is null)
        {
            context.Values.Add(new SettingValue(key, place.Level, place.StoredTarget, json));
        }
        else if (string.Equals(Normalise(row.ValueJson), json, StringComparison.Ordinal))
        {
            // The same value again: nothing changes, so nothing is written or audited.
            return Result.Success();
        }
        else
        {
            row.Change(json);
        }

        context.History.Add(new SettingChange(key, place.Level, place.StoredTarget, SettingChange.SetKind, before, json));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(SettingsAuditEvents.ValueChanged, AuditOutcome.Success, current.Id, key, Details(key, place, before, json), "setting"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await refresher.RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Removes the value at this place, so the inherited one applies (TF-058 AC2). Nothing set here: nothing to do.</summary>
    public async Task<Result> ResetAsync(string key, SettingPlace place, CancellationToken cancellationToken)
    {
        var checkedSetting = await CheckAsync(key, place, cancellationToken).ConfigureAwait(false);
        if (checkedSetting.IsFailure)
        {
            return checkedSetting.Error;
        }

        if (RecentEnough(checkedSetting.Value.Definition) is { } stale)
        {
            return stale;
        }

        if (await HeldAsync(checkedSetting.Value.Definition, place, null, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(key, cancellationToken).ConfigureAwait(false);
        if (await FindAsync(key, place, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return Result.Success();
        }

        var before = row.ValueJson;
        context.Values.Remove(row);
        context.History.Add(new SettingChange(key, place.Level, place.StoredTarget, SettingChange.ResetKind, before, null));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(SettingsAuditEvents.ValueReset, AuditOutcome.Success, current.Id, key, Details(key, place, before, null), "setting"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await refresher.RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The value set at a place now, as a pending change compares it (ADR-0052): <c>{"value": ...}</c>, null when nothing
    /// is set there and the inherited value applies.
    /// </summary>
    public async Task<JsonElement> StateAsync(string key, SettingPlace place, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(place);
        var row = await context.Values.AsNoTracking().SingleOrDefaultAsync(v => v.Key == key && v.Level == place.Level && v.TargetId == place.StoredTarget, cancellationToken).ConfigureAwait(false);
        return State(row is null ? null : Normalise(row.ValueJson));
    }

    public static string Where(SettingLevel level) => level switch
    {
        SettingLevel.Tenant => "for the whole organisation",
        SettingLevel.Unit => "for an organisation unit",
        SettingLevel.User => "by a person for themselves",
        _ => "here",
    };

    private static string Allowed(SettingDefinition setting) =>
        string.Join(" or ", new[] { SettingLevel.Tenant, SettingLevel.Unit, SettingLevel.User }.Where(setting.Allows).Select(Where));

    /// <summary>
    /// A setting of a sensitive area waits for a second administrator when the tenant lists the area (TF-068, ADR-0052):
    /// the save or reset becomes a pending change with the value before and after, and nothing is written yet.
    /// </summary>
    private async Task<Error?> HeldAsync(SettingDefinition setting, SettingPlace place, string? after, CancellationToken cancellationToken)
    {
        if (fourEyes is null || setting.GovernedArea is not { } area || !await fourEyes.AppliesAsync(area, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var reset = after is null;
        var payload = ChangeJson.Of(new SettingChangePayload(setting.Key, place.Level.ToString().ToLowerInvariant(), place.TargetId, reset ? null : JsonDocument.Parse(after!).RootElement.Clone()));
        var title = reset ? $"Reset the setting {setting.Key} {Where(place.Level)}" : $"Change the setting {setting.Key} {Where(place.Level)}";
        var scope = place.Level == SettingLevel.Unit && place.TargetId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant;
        var proposal = new ChangeProposal(
            area,
            reset ? ResetKind : SetKind,
            $"setting:{setting.Key}:{place.Level.ToString().ToLowerInvariant()}:{place.StoredTarget:N}",
            title,
            await StateAsync(setting.Key, place, cancellationToken).ConfigureAwait(false),
            State(after),
            payload,
            SettingsPermissions.Manage.Name,
            scope);
        return await fourEyes.HoldAsync(proposal, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement State(string? json) => ChangeJson.Of(new { Value = json is null ? (JsonElement?)null : JsonDocument.Parse(json).RootElement.Clone() });

    private Task<SettingValue?> FindAsync(string key, SettingPlace place, CancellationToken cancellationToken) =>
        context.Values.SingleOrDefaultAsync(v => v.Key == key && v.Level == place.Level && v.TargetId == place.StoredTarget, cancellationToken);

    // Two saves of one key at once: the second waits, then sees the first's row instead of inserting a second one.
    private Task LockAsync(string key, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtext({"settings:" + key}))", cancellationToken);

    private static string Normalise(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, Compact);
    }

    private static Dictionary<string, string> Details(string key, SettingPlace place, string? before, string? after)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["scope"] = place.Level.ToString().ToLowerInvariant(),
            ["before"] = before is null ? "none" : Normalise(before),
            ["after"] = after ?? "none",
        };
        if (place.Level != SettingLevel.Tenant && place.TargetId is { } target)
        {
            details[place.Level == SettingLevel.Unit ? "unit" : "user"] = target.ToString("D");
        }

        return details;
    }
}
