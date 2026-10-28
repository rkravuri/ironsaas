using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Fleet.Services;

/// <summary>The store tokens of the control plane's API and of the fleet, as <c>prepare-control</c> keeps them for the operator's start.</summary>
public sealed record ControlPlaneTokens(string ApiToken, string FleetToken);

/// <summary>
/// The control plane's API and the fleet each get their own store token, with only the grants each needs (ADR-0054, T9a's
/// review). The API, which faces the network, reads its own secret, writes the tenants' entitlements and flags and the
/// platform's documents, takes a job's handover once, and uses the control plane's own keys. The fleet administers the
/// tenants: their secrets, policies, tokens (through the token role, which makes only tenant policies' tokens) and keys,
/// and reads its own secret, the control plane's database passwords and the migrate secret. So the API never holds the
/// fleet's administrative database connection, the storage account key or the right to make tokens. Made with the
/// store's root token when the operator prepares the control plane; tokens that still work are kept.
/// </summary>
public sealed class ControlPlaneAccess(ISecretStoreAdmin admin, ISecretStore secrets, IKeyAdministration keys, SecretStoreOptions store)
{
    public const string ApiTokenKey = "api_token";
    public const string FleetTokenKey = "fleet_token";

    /// <summary>The tokens last a year; <c>make up-control</c> makes new ones when they no longer open the store.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(365);

    public async Task<ControlPlaneTokens> PrepareAsync(CancellationToken cancellationToken)
    {
        foreach (var purpose in KeyPurposes.All)
        {
            await keys.EnsureAsync(KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, purpose), KeyKinds.Of(purpose), cancellationToken).ConfigureAwait(false);
        }

        await admin.CreatePolicyAsync(ControlPlaneNames.ApiPolicy, ApiRules(store), cancellationToken).ConfigureAwait(false);
        await admin.CreatePolicyAsync(ControlPlaneNames.FleetPolicy, FleetRules(store), cancellationToken).ConfigureAwait(false);
        await admin.EnsureTokenRoleAsync(ControlPlaneNames.TokenRole, ["tenant-*"], cancellationToken).ConfigureAwait(false);

        var kept = await secrets.ReadAsync(ControlPlaneNames.TokensSecretPath, cancellationToken).ConfigureAwait(false);
        var api = await KeptOrNewAsync(kept?.GetValueOrDefault(ApiTokenKey), ControlPlaneNames.ApiPolicy, "control-api", cancellationToken).ConfigureAwait(false);
        var fleet = await KeptOrNewAsync(kept?.GetValueOrDefault(FleetTokenKey), ControlPlaneNames.FleetPolicy, "fleet", cancellationToken).ConfigureAwait(false);
        if (kept?.GetValueOrDefault(ApiTokenKey) != api || kept?.GetValueOrDefault(FleetTokenKey) != fleet)
        {
            await secrets.WriteAsync(ControlPlaneNames.TokensSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { [ApiTokenKey] = api, [FleetTokenKey] = fleet }, cancellationToken).ConfigureAwait(false);
        }

        return new ControlPlaneTokens(api, fleet);
    }

    /// <summary>The control plane's API's grants.</summary>
    public static IReadOnlyList<StorePolicyRule> ApiRules(SecretStoreOptions store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var kv = store.Mount.Trim('/');
        var transit = store.TransitMount.Trim('/');
        string[] read = ["read"];
        string[] write = ["create", "update"];
        return
        [
            new($"{kv}/data/{ControlPlaneNames.UnitSecretPath}", read),
            new($"{kv}/metadata/{ControlPlaneNames.UnitSecretPath}", read),
            new($"{kv}/data/control/settings/*", read),
            new($"{kv}/data/control/sso/*", ["create", "read", "update", "delete"]),
            new($"{kv}/metadata/control/sso/*", ["read", "delete"]),
            new($"{kv}/data/tenants/+/entitlements", write),
            new($"{kv}/data/tenants/+/flags", write),
            new($"{kv}/data/platform/flags", write),
            new($"{kv}/data/platform/documents", write),
            new($"{kv}/data/tenants/+/handover/+", read),
            new($"{kv}/metadata/tenants/+/handover/+", ["read", "delete"]),
            new($"{transit}/encrypt/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Data)}", ["update"]),
            new($"{transit}/decrypt/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Data)}", ["update"]),
            new($"{transit}/rewrap/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Data)}", ["update"]),
            new($"{transit}/hmac/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Index)}", ["update"]),
            new($"{transit}/sign/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Signing)}", ["update"]),
            new($"{transit}/hmac/{KeyPurposes.KeyName(ControlPlaneNames.KeyPrefix, KeyPurpose.Signing)}", ["update"]),
            new($"{transit}/keys/{ControlPlaneNames.KeyPrefix}-*", read),
        ];
    }

    /// <summary>The fleet's grants: everything a tenant's provisioning, update, rotation and verification needs.</summary>
    public static IReadOnlyList<StorePolicyRule> FleetRules(SecretStoreOptions store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var kv = store.Mount.Trim('/');
        var transit = store.TransitMount.Trim('/');
        return
        [
            new($"{kv}/data/{ControlPlaneNames.FleetSecretPath}", ["create", "read", "update"]),
            new($"{kv}/metadata/{ControlPlaneNames.FleetSecretPath}", ["read"]),
            new($"{kv}/data/{ControlPlaneNames.DatabaseSecretPath}", ["read"]),
            new($"{kv}/data/{ControlPlaneNames.MigrateSecretPath}", ["read"]),
            new($"{kv}/data/tenants/*", ["create", "read", "update", "delete"]),
            new($"{kv}/metadata/tenants/*", ["create", "read", "update", "delete", "list"]),
            new($"{kv}/data/platform/*", ["create", "read", "update"]),
            new($"{kv}/metadata/platform/*", ["read"]),
            new("sys/policies/acl/tenant-*", ["create", "read", "update"]),
            new($"auth/token/create/{ControlPlaneNames.TokenRole}", ["update"]),
            new($"{transit}/keys/tenant-*", ["create", "read", "update"]),
            new($"{transit}/keys/{ControlPlaneNames.KeyPrefix}-*", ["read"]),
        ];
    }

    private async Task<string> KeptOrNewAsync(string? kept, string policy, string name, CancellationToken cancellationToken)
    {
        if (kept is not null && await admin.IsTokenValidAsync(kept, cancellationToken).ConfigureAwait(false))
        {
            return kept;
        }

        return await admin.CreateTokenAsync([policy], name, TokenLifetime, cancellationToken).ConfigureAwait(false);
    }
}
