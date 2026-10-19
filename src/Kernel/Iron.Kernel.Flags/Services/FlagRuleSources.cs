using System.Text.Json;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Flags.Services;

/// <summary>The rules of both paths as one read gave them; a path with nothing at it is null, which is an answer: no rules there.</summary>
public sealed record FlagRuleSet(FlagRulesDocument? Platform, FlagRulesDocument? Tenant)
{
    public IEnumerable<FlagRuleEntry> Rules => (Platform?.Rules ?? []).Concat(Tenant?.Rules ?? []);

    public string? Attribute(string name) => Tenant?.Attributes?.GetValueOrDefault(name);
}

/// <summary>Where a unit's flag rules come from. A read that fails throws, so the refresh keeps the last known rules.</summary>
public interface IFlagRuleSource
{
    string Describe();

    Task<FlagRuleSet> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The store (ADR-0035): <c>platform/flags</c>, shared by every unit, and the tenant's <c>tenants/&lt;slug&gt;/flags</c>, both
/// written by the control plane and readable with the unit's token. Both are read in one go; either failing fails the read.
/// </summary>
public sealed class StoreFlagRuleSource(ISecretStore store, SecretStoreOptions options) : IFlagRuleSource
{
    public string TenantPath => $"{options.Path?.Trim('/')}/{FlagRulesDocument.SecretName}";

    public string Describe() => $"the store at {options.Address}, {options.Mount}/{FlagRulesDocument.PlatformPath} and {options.Mount}/{TenantPath}";

    public async Task<FlagRuleSet> ReadAsync(CancellationToken cancellationToken)
    {
        var platform = await ReadAsync(FlagRulesDocument.PlatformPath, cancellationToken).ConfigureAwait(false);
        var tenant = await ReadAsync(TenantPath, cancellationToken).ConfigureAwait(false);
        return new FlagRuleSet(platform, tenant);
    }

    private async Task<FlagRulesDocument?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var secret = await store.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        return secret.TryGetValue(FlagRulesDocument.DocumentKey, out var json)
            ? FlagRulesDocument.Parse(json)
            : throw new FormatException($"The secret {options.Mount}/{path} has no '{FlagRulesDocument.DocumentKey}' key.");
    }
}

/// <summary>The <c>Flags:Rules</c> section as global rules, for a host that has no store or is no tenant.</summary>
public sealed class ConfigurationFlagRuleSource(FlagsOptions options) : IFlagRuleSource
{
    public string Describe() => "the Flags configuration section";

    public Task<FlagRuleSet> ReadAsync(CancellationToken cancellationToken)
    {
        var rules = options.Rules
            .Select((rule, index) => new FlagRuleEntry(Guid.Empty, rule.Flag, FlagScope.Global, null, null, null, null, FlagCondition.Always, JsonDocument.Parse(rule.Value).RootElement.Clone(), index, rule.Reason, DateTimeOffset.UnixEpoch))
            .ToList();
        var platform = new FlagRulesDocument(FlagRulesDocument.CurrentFormat, FlagRulesDocument.PlatformPath, 0, DateTimeOffset.UnixEpoch, null, rules);
        return Task.FromResult(new FlagRuleSet(platform, null));
    }
}
