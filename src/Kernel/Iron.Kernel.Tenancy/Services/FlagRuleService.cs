using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>A rule as the operator sends it: scope, target, condition, value and, for a kill switch, a reason.</summary>
public sealed record FlagRuleInput(FlagScope Scope, string? Region, string? Tenant, Guid? UnitId, Guid? UserId, FlagCondition? Condition, JsonElement Value, string? Reason);

/// <summary>A flag's rules and the revision they are at: a replacement names the revision it read, so two operators cannot overwrite each other.</summary>
public sealed record FlagRuleList(string Flag, int Revision, IReadOnlyList<FlagRuleEntry> Rules);

/// <summary>
/// The rules of feature flags in the control plane (TF-050, TF-051, TF-053; ADR-0035). A flag's rules are replaced
/// together, in one transaction with the append-only history (before, after, the operator, the reason) and the audit
/// event; the store paths the change touches are marked behind, and the publisher writes them after the commit. A rule
/// for a flag the catalogue does not have, a value of another type, or a target that does not exist is refused. A kill
/// switch needs a reason; who may flip one is the endpoint's check (tenancy.kill_switches.manage).
/// </summary>
public sealed class FlagRuleService(
    TenancyDbContext context,
    UnitCatalogue catalogue,
    FlagPublications publications,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current)
{
    public async Task<Result<FlagRuleList>> ListAsync(string key, CancellationToken cancellationToken)
    {
        if (catalogue.Catalogue.FindFlag(key) is null && !await context.FlagRules.AnyAsync(r => r.FlagKey == key, cancellationToken).ConfigureAwait(false))
        {
            return Error.NotFound(TenancyErrors.FlagUnknown, $"There is no flag '{key}' in the catalogue of this build.");
        }

        var rules = await RulesAsync(key, asTracking: false, cancellationToken).ConfigureAwait(false);
        return new FlagRuleList(key, await RevisionAsync(key, cancellationToken).ConfigureAwait(false), [.. rules.Select(r => r.ToEntry())]);
    }

    /// <summary>Replaces the flag's rules; <paramref name="expectedRevision"/> null replaces whatever is there.</summary>
    public async Task<Result<FlagRuleList>> ReplaceAsync(string key, IReadOnlyList<FlagRuleInput> inputs, int? expectedRevision, string? reason, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var flag = catalogue.Catalogue.FindActiveFlag(key);
        if (flag is null)
        {
            return Error.Validation(TenancyErrors.FlagUnknown, $"'{key}' is not a flag of this build: a rule names a flag its module declares (make catalogue).", new FieldError("flag", "unknown", $"'{key}' is not a flag of this build."));
        }

        if (flag.IsKillSwitch && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(TenancyErrors.KillSwitchReasonRequired, $"'{key}' is a kill switch: say why it changes, in words the tenants' administrators will read.", new FieldError("reason", "required", "Give the reason."));
        }

        // A kill switch goes off when its rule is removed. A rule that says false would read as an exemption, and none is
        // possible: the switch is on wherever any applicable rule says so (ADR-0035), so such a rule is refused, not kept.
        if (flag.IsKillSwitch && inputs.Any(input => input.Value.ValueKind == JsonValueKind.False))
        {
            return Error.Validation(
                TenancyErrors.KillSwitchFalseRule,
                $"'{key}' is a kill switch, and a rule for it can only say true. To turn it off for the platform or a tenant, remove that rule: make kill-switch ... ON=false. No rule exempts a tenant from a switch that is on.",
                new FieldError("rules", "false_rule", "A kill switch's rule says true; remove the rule to turn the switch off."));
        }

        var problems = await ProblemsAsync(flag.Type, inputs, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            var code = problems.Any(p => p.Code == "type") ? TenancyErrors.FlagValueType : TenancyErrors.FlagRuleInvalid;
            return Error.Validation(code, $"The rules of '{key}' are refused: {string.Join(" ", problems.Select(p => p.Message))}", problems);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({FlagLockKey})", cancellationToken).ConfigureAwait(false);
        var revision = await RevisionAsync(key, cancellationToken).ConfigureAwait(false);
        if (expectedRevision is { } expected && expected != revision)
        {
            return Error.Conflict(TenancyErrors.FlagRulesChanged, $"The rules of '{key}' changed since you read them (revision {expected}, now {revision}). Read them again, then change them.");
        }

        var before = await RulesAsync(key, asTracking: true, cancellationToken).ConfigureAwait(false);
        var beforeJson = FlagRulesDocument.Serialize(before.Select(r => r.ToEntry()).ToList());

        // A rule that is the same before and after keeps its row, so its "since" (a kill switch's banner) stays true.
        var unused = new List<FlagRule>(before);
        var after = new List<FlagRule>();
        for (var position = 0; position < inputs.Count; position++)
        {
            var wanted = new FlagRule(key, Entry(key, inputs[position]), position);
            var same = unused.FirstOrDefault(r => r.SameAs(wanted));
            if (same is null)
            {
                context.FlagRules.Add(wanted);
                after.Add(wanted);
            }
            else
            {
                unused.Remove(same);
                same.MoveTo(position);
                after.Add(same);
            }
        }

        context.FlagRules.RemoveRange(unused);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var afterJson = FlagRulesDocument.Serialize(after.Select(r => r.ToEntry()).ToList());
        context.FlagHistory.Add(new FlagRuleChange(key, kind, revision + 1, beforeJson, afterJson, reason?.Trim()));
        foreach (var path in unused.Concat(after).Select(r => r.StorePath).Distinct(StringComparer.Ordinal))
        {
            await publications.TouchAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flag"] = key,
            ["revision"] = (revision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rules_before"] = before.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rules_after"] = after.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var action = kind == FlagRuleChange.KillSwitchKind ? TenancyAuditEvents.KillSwitchFlipped : TenancyAuditEvents.FlagRulesChanged;
        await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, current.Id, key, details, "flag", null, reason?.Trim()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FlagRuleList(key, revision + 1, [.. after.Select(r => r.ToEntry())]);
    }

    /// <summary>
    /// A kill switch on or off for the platform, or for one tenant (TF-051): its unconditional rule at that scope is put
    /// or taken away, the other rules stay, and the change is in the history as a flip with the operator's reason.
    /// </summary>
    public async Task<Result<FlagRuleList>> SetKillSwitchAsync(string path, bool on, string? reason, string? tenant, CancellationToken cancellationToken)
    {
        var key = Flag.KillSwitchPrefix + path;
        if (catalogue.Catalogue.FindActiveFlag(key) is not { IsKillSwitch: true })
        {
            return Error.NotFound(TenancyErrors.KillSwitchUnknown, $"There is no kill switch '{path}'. The kill switches are: {string.Join(", ", catalogue.Catalogue.Flags.Where(f => f.IsKillSwitch).Select(f => f.Key[Flag.KillSwitchPrefix.Length..]))}.");
        }

        var scope = tenant is null ? FlagScope.Global : FlagScope.Tenant;
        var rules = await RulesAsync(key, asTracking: false, cancellationToken).ConfigureAwait(false);
        var kept = rules
            .Where(r => !(r.Scope == scope && r.ConditionKind == FlagConditionKind.Always && string.Equals(r.TenantSlug, tenant, StringComparison.Ordinal)))
            .Select(r => new FlagRuleInput(r.Scope, r.Region, r.TenantSlug, r.UnitId, r.UserId, r.Condition, JsonDocument.Parse(r.ValueJson).RootElement.Clone(), r.Reason))
            .ToList();
        if (on)
        {
            kept.Insert(0, new FlagRuleInput(scope, null, tenant, null, null, FlagCondition.Always, JsonSerializer.SerializeToElement(true), reason));
        }

        return await ReplaceAsync(key, kept, null, reason, FlagRuleChange.KillSwitchKind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The key of the transaction-scoped advisory lock every change of flag rules takes.</summary>
    public const long FlagLockKey = 0x0F1A_0000_0001;

    /// <summary>The revision of a flag's rules: the number of changes it has had.</summary>
    public async Task<int> RevisionAsync(string key, CancellationToken cancellationToken) =>
        await context.FlagHistory.Where(c => c.FlagKey == key).MaxAsync(c => (int?)c.Revision, cancellationToken).ConfigureAwait(false) ?? 0;

    private async Task<List<FlagRule>> RulesAsync(string key, bool asTracking, CancellationToken cancellationToken)
    {
        var query = context.FlagRules.Where(r => r.FlagKey == key);
        if (!asTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(r => r.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FlagRuleEntry Entry(string key, FlagRuleInput input) =>
        new(Guid.Empty, key, input.Scope, Text(input.Region), Text(input.Tenant), input.UnitId, input.UserId, input.Condition ?? FlagCondition.Always, input.Value, 0, input.Reason, default);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<List<FieldError>> ProblemsAsync(FlagType type, IReadOnlyList<FlagRuleInput> inputs, CancellationToken cancellationToken)
    {
        var problems = new List<FieldError>();
        var tenants = inputs.Select(i => Text(i.Tenant)).Where(t => t is not null).Distinct().ToList();
        var known = await context.Tenants.AsNoTracking().Where(t => tenants.Contains(t.Slug)).Select(t => t.Slug).ToListAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"rules[{i}]";
            if (!Flag.Fits(type, input.Value))
            {
                problems.Add(new FieldError($"{field}.value", "type", $"Rule {i + 1}: the value is not a {type.ToString().ToLowerInvariant()}."));
            }

            var tenant = Text(input.Tenant);
            var target = input.Scope switch
            {
                FlagScope.Global when tenant is null && Text(input.Region) is null && input.UnitId is null && input.UserId is null => null,
                FlagScope.Global => "a global rule names no region, tenant, unit or user",
                FlagScope.Region when Text(input.Region) is not null && tenant is null => null,
                FlagScope.Region => "a region rule names a region and nothing else",
                FlagScope.Tenant when tenant is not null && input.UnitId is null && input.UserId is null => null,
                FlagScope.Unit when tenant is not null && input.UnitId is { } unit && unit != Guid.Empty && input.UserId is null => null,
                FlagScope.User when tenant is not null && input.UserId is { } user && user != Guid.Empty && input.UnitId is null => null,
                _ => $"a {input.Scope.ToString().ToLowerInvariant()} rule names its tenant{(input.Scope == FlagScope.Tenant ? string.Empty : $" and its {input.Scope.ToString().ToLowerInvariant()}")}",
            };
            if (target is not null)
            {
                problems.Add(new FieldError(field, "target", $"Rule {i + 1}: {target}."));
            }
            else if (tenant is not null && !known.Contains(tenant))
            {
                problems.Add(new FieldError($"{field}.tenant", "unknown", $"Rule {i + 1}: there is no tenant '{tenant}'."));
            }

            var condition = input.Condition;
            if (condition is { Kind: FlagConditionKind.Percentage } && (condition.Percentage is not (>= 0 and <= 100) || condition.Of is null))
            {
                problems.Add(new FieldError($"{field}.condition", "percentage", $"Rule {i + 1}: a percentage is from 0 to 100, of tenants or of users."));
            }

            if (condition is { Kind: FlagConditionKind.Attribute } && (condition.Attribute is null || !FlagAttributes.All.Contains(condition.Attribute, StringComparer.Ordinal) || condition.Values is not { Count: > 0 }))
            {
                problems.Add(new FieldError($"{field}.condition", "attribute", $"Rule {i + 1}: an attribute rule names one of {string.Join(", ", FlagAttributes.All)} and at least one value."));
            }
        }

        return problems;
    }
}
