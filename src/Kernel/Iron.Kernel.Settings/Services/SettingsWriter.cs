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
    SecretStoreOptions? store = null)
{
    private static readonly JsonSerializerOptions Compact = new();

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

        if (setting.IsSecret && store?.Path is { Length: > 0 } unitPath && !value.GetString()!.StartsWith($"{unitPath.Trim('/')}/settings/", StringComparison.Ordinal))
        {
            var words = $"A path under {unitPath.Trim('/')}/settings/, the part of the store this unit reads.";
            return Error.Validation(SettingsErrors.ValueInvalid, $"The value for '{key}' is refused. {words}", new FieldError("value", "invalid", words));
        }

        var json = JsonSerializer.Serialize(value, Compact);
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

    public static string Where(SettingLevel level) => level switch
    {
        SettingLevel.Tenant => "for the whole organisation",
        SettingLevel.Unit => "for an organisation unit",
        SettingLevel.User => "by a person for themselves",
        _ => "here",
    };

    private static string Allowed(SettingDefinition setting) =>
        string.Join(" or ", new[] { SettingLevel.Tenant, SettingLevel.Unit, SettingLevel.User }.Where(setting.Allows).Select(Where));

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
