using System.Text.Json;
using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Flags.Services;

namespace Iron.Kernel.Flags.Api;

/// <summary>A flag's value for the signed-in person, and why: the shell's OpenFeature provider answers from these (TF-054).</summary>
public sealed record FlagValueEntry(string Key, FlagType Type, JsonElement Value, string Reason);

/// <summary>A kill switch that is on, for the administrators' banner: which path, since when, and the operator's reason.</summary>
public sealed record KillSwitchEntry(string Path, string Flag, DateTimeOffset Since, string? Reason);

/// <summary>
/// The <c>flags</c> part of <c>/me/context</c>: whether rules are known (false: the code defaults), when they were confirmed,
/// every flag of this build evaluated for the person, and, for an administrator only, the kill switches that are on.
/// </summary>
public sealed record ContextFlags(bool Known, DateTimeOffset? ConfirmedAt, IReadOnlyList<FlagValueEntry> Values, IReadOnlyList<KillSwitchEntry> KillSwitches);

/// <summary>
/// The <c>flags</c> part of <c>GET /api/v1/me/context</c> (TF-054, ADR-0035), evaluated by the same provider the API's
/// OpenFeature client uses. An administrator is a principal who holds a <c>.manage</c> permission for the whole tenant:
/// they get the kill switches with the operator's reason; nobody else does.
/// </summary>
public sealed class FlagsContextPart(IronFlagProvider provider, FlagSubjects subjects, FlagRulesCache cache, LicensingCatalogue catalogue, IRoleAssignments? roles = null) : IContextPart
{
    public const string PartName = "flags";

    public string Name => PartName;

    public async Task<object> BuildAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var subject = await subjects.ForAsync(principalId, cancellationToken).ConfigureAwait(false);
        var values = new List<FlagValueEntry>();
        var killed = new List<KillSwitchEntry>();
        foreach (var entry in catalogue.Flags.Where(f => f.Status == FlagStatus.Active))
        {
            var flag = provider.Declaration(entry.Key)!;
            var result = provider.Evaluate(flag, subject);
            values.Add(new FlagValueEntry(flag.Key, flag.Type, result.Value, result.Reason));
            if (flag.IsKillSwitch && result.Value.ValueKind == JsonValueKind.True && result.Rule is { } rule)
            {
                killed.Add(new KillSwitchEntry(flag.KillSwitchPath!, flag.Key, rule.ChangedAt, rule.Reason));
            }
        }

        var administrator = killed.Count > 0 && roles is not null
            && (await roles.PermissionsOfAsync(principalId, cancellationToken).ConfigureAwait(false)).Any(p => p.EndsWith(".manage", StringComparison.Ordinal));
        return new ContextFlags(cache.Current is not null, cache.Current?.ConfirmedAt, values, administrator ? killed : []);
    }
}
