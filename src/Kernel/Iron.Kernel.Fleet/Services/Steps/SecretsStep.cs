using System.Globalization;
using System.Security.Cryptography;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// Step 4: the tenant's keys in the key service (<c>tenant-&lt;slug&gt;-data</c>, <c>-index</c>, <c>-signing</c>, ADR-0054),
/// made once and recorded in the control plane with the database password's version; then the unit's secret
/// (<c>tenants/&lt;slug&gt;/unit</c>): the app connection string, the app role, the signed container URL and the unit's key
/// family, and no key: the factor key of ADR-0047 left a unit secret made before T9b for the fleet secret, which hands it
/// once to the seed that moves the secrets sealed under it. Then a policy that uses the tenant's keys and nothing more
/// (encrypt, decrypt and re-wrap with the data key, hash with the index key, sign and hash with the signing key, read
/// their versions), reads that path and the tenant's entitlements
/// (<c>tenants/&lt;slug&gt;/entitlements</c>, ADR-0034) and its flag rules (<c>tenants/&lt;slug&gt;/flags</c> and the shared
/// <c>platform/flags</c>, ADR-0035), and that manages its single sign-on providers' client secrets
/// (<c>tenants/&lt;slug&gt;/sso/*</c>, ADR-0048), and nothing else, and one unit token with it, kept at
/// <c>tenants/&lt;slug&gt;/fleet</c> for the unit's environment (TF-073, TF-210).
/// An update runs it too (ADR-0028): the unit secret is written from what the two steps before it hold now, the policy
/// is written again, so a unit made before a path was added reads it with its old token, and a lost unit token means a
/// new token, which the unit step then puts into the unit's environment. The entitlements are written again as well,
/// so a store that lost them heals with the unit. The unit's own client in the control plane (ADR-0050), which sends its
/// audit anchors, is made or given its secret again here, and the unit secret carries its address, id and secret.
/// </summary>
public sealed class SecretsStep(
    ISecretStore secrets,
    ISecretStoreAdmin admin,
    ITenantEntitlements entitlements,
    ITenantFlags flags,
    IServiceAccounts? serviceAccounts = null,
    ITenantAuditChains? chains = null,
    IKeyAdministration? keys = null,
    ITenantKeys? tenantKeys = null) : IProvisioningStep
{
    public const string UnitTokenKey = "unit_token";

    /// <summary>The unit's control-plane client secret, kept in the fleet secret so an update keeps it (ADR-0050).</summary>
    public const string ControlClientSecretKey = "control_client_secret";

    /// <summary>The configuration key of the second factors' key of ADR-0047, retired by ADR-0054: no unit secret carries it now.</summary>
    public const string FactorKeyKey = "Identity:FactorKey";

    /// <summary>
    /// The retired factor key, kept in the fleet secret, which the unit's token cannot read, until the seed has moved the
    /// secrets sealed under it onto the key service (ADR-0054).
    /// </summary>
    public const string LegacyFactorKeyKey = "legacy_factor_key";

    /// <summary>The configuration key of the unit's key family in the key service (ADR-0054): <c>tenant-&lt;slug&gt;</c>.</summary>
    public const string KeyPrefixKey = "Keys:Prefix";

    public string Name => StepNames.Secrets;

    public int Order => 4;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The database step has not run: no database secret.");
        var storage = await secrets.ReadAsync(context.StorageSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The storage step has not run: no storage secret.");
        var current = await secrets.ReadAsync(context.UnitSecretPath, cancellationToken).ConfigureAwait(false);
        var prefix = KeyPurposes.TenantPrefix(context.Slug);
        var keysMade = await KeysAsync(context, prefix, database, cancellationToken).ConfigureAwait(false);
        await KeepRetiredFactorKeyAsync(context, current, cancellationToken).ConfigureAwait(false);

        var unit = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Database"] = context.ConnectionString(context.AppRole, database[DatabaseStep.AppPasswordKey]),
            ["Database:AppRole"] = context.AppRole,
            ["Storage:ContainerSasUrl"] = storage[StorageStep.ContainerSasUrlKey],

            // The unit's keys are the key service's, under this family; the unit holds none of them (ADR-0054).
            [KeyPrefixKey] = prefix,

            // migrate grants the verifier's role its reads of the chain (ADR-0050); the unit never connects with it.
            ["Database:AuditReaderRole"] = context.AuditReaderRole,
        };

        var client = await ControlClientAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is { } made)
        {
            unit["ControlPlane:Address"] = context.Options.ControlPlane.UnitAddress;
            unit["ControlPlane:ClientId"] = context.ControlClientId;
            unit["ControlPlane:ClientSecret"] = made;
        }

        await secrets.WriteAsync(context.UnitSecretPath, unit, cancellationToken).ConfigureAwait(false);

        // On every run: the token carries the policy by name, so writing it again widens what an existing token may read.
        await admin.CreateReadPolicyAsync(context.UnitPolicy, [context.UnitSecretPath, context.EntitlementsSecretPath, context.FlagsSecretPath, ProvisioningContext.PlatformFlagsPath, ProvisioningContext.PlatformDocumentsPath, context.SettingsSecretPaths], cancellationToken, [context.SsoSecretPaths], UnitKeyGrants(prefix)).ConfigureAwait(false);

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false);
        var tokenCreated = false;
        if (fleet?.GetValueOrDefault(UnitTokenKey) is null)
        {
            var token = await admin.CreateTokenAsync([context.UnitPolicy], $"unit-{context.Slug}", TimeSpan.FromDays(context.Options.Secrets.UnitTokenValidDays), cancellationToken).ConfigureAwait(false);
            // The smoke client's secret, if the fleet secret still has it, stays beside the new token.
            var updated = fleet is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(fleet, StringComparer.Ordinal);
            updated[UnitTokenKey] = token;
            await secrets.WriteAsync(context.FleetSecretPath, updated, cancellationToken).ConfigureAwait(false);
            tokenCreated = true;
        }

        var published = await entitlements.PublishAsync(context.Slug, cancellationToken).ConfigureAwait(false);
        var flagsPublished = await flags.PublishAsync(context.Slug, cancellationToken).ConfigureAwait(false);
        return $"keys {keysMade}; unit secret written with {unit.Count} keys; policy {context.UnitPolicy} uses the tenant's keys, reads the unit secret, the entitlements, the flag rules and the settings' secrets and manages the single sign-on secrets; unit token {(tokenCreated ? "created" : "kept")}; entitlements {(published ? "written" : "left to the reconcile")}; flag rules {(flagsPublished ? "written" : "left to the reconcile")}";
    }

    /// <summary>What a unit's token may do with its tenant's keys (ADR-0054): use them, never rotate, retire or export them.</summary>
    public static IReadOnlyList<KeyGrant> UnitKeyGrants(string prefix) =>
    [
        new(KeyPurposes.KeyName(prefix, KeyPurpose.Data), KeyUses.Encrypt | KeyUses.Decrypt | KeyUses.Rewrap | KeyUses.Read),
        new(KeyPurposes.KeyName(prefix, KeyPurpose.Index), KeyUses.Hash | KeyUses.Read),
        new(KeyPurposes.KeyName(prefix, KeyPurpose.Signing), KeyUses.Sign | KeyUses.Hash | KeyUses.Read),
    ];

    /// <summary>
    /// Makes the tenant's three keys where they do not exist yet and records every key's versions, and the database
    /// password's, in the control plane (ADR-0054). Where the host has no key service, as in the fleet's own tests, nothing.
    /// </summary>
    private async Task<string> KeysAsync(ProvisioningContext context, string prefix, IReadOnlyDictionary<string, string> database, CancellationToken cancellationToken)
    {
        if (keys is null)
        {
            return "left alone";
        }

        foreach (var purpose in KeyPurposes.All)
        {
            var name = KeyPurposes.KeyName(prefix, purpose);
            await keys.EnsureAsync(name, KeyKinds.Of(purpose), cancellationToken).ConfigureAwait(false);
            if (tenantKeys is not null && await keys.DescribeAsync(name, cancellationToken).ConfigureAwait(false) is { } versions)
            {
                await tenantKeys.RecordKeyAsync(context.Slug, purpose.Word(), versions.Latest, versions.Minimum, cancellationToken).ConfigureAwait(false);
            }
        }

        if (tenantKeys is not null)
        {
            var password = int.TryParse(database.GetValueOrDefault(DatabasePasswordRotation.VersionKey), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 1;
            await tenantKeys.RecordKeyAsync(context.Slug, TenantKeyNames.Database, password, password, cancellationToken).ConfigureAwait(false);
        }

        return $"{prefix}-data, -index and -signing in place";
    }

    /// <summary>
    /// A unit secret made before T9b holds the factor key of ADR-0047: it moves to the fleet secret, which the unit cannot
    /// read, until the seed moves the secrets sealed under it (ADR-0054); the unit secret written next leaves it out.
    /// </summary>
    private async Task KeepRetiredFactorKeyAsync(ProvisioningContext context, IReadOnlyDictionary<string, string>? current, CancellationToken cancellationToken)
    {
        if (current?.GetValueOrDefault(FactorKeyKey) is not { Length: > 0 } retired)
        {
            return;
        }

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false);
        if (fleet?.ContainsKey(LegacyFactorKeyKey) == true)
        {
            return;
        }

        var updated = fleet is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(fleet, StringComparer.Ordinal);
        updated[LegacyFactorKeyKey] = retired;
        await secrets.WriteAsync(context.FleetSecretPath, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The unit's client in the control plane (ADR-0050): a service account with the unit-client role, bound to the
    /// tenant, its secret kept in the fleet secret. Null where the host has no Identity, such as the fleet's own tests.
    /// </summary>
    private async Task<string?> ControlClientAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        if (serviceAccounts is null || chains is null)
        {
            return null;
        }

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false);
        var secret = fleet?.GetValueOrDefault(ControlClientSecretKey);
        if (secret is null)
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var updated = fleet is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(fleet, StringComparer.Ordinal);
            updated[ControlClientSecretKey] = secret;
            await secrets.WriteAsync(context.FleetSecretPath, updated, cancellationToken).ConfigureAwait(false);
        }

        var account = await serviceAccounts.EnsureAsync(context.ControlClientId, $"Unit of {context.Slug}", SystemRoles.UnitClient, secret, cancellationToken).ConfigureAwait(false);
        if (account.IsFailure)
        {
            throw new InvalidOperationException($"The unit's control-plane client could not be made: {account.Error.Message}");
        }

        await chains.BindUnitClientAsync(context.Slug, account.Value, cancellationToken).ConfigureAwait(false);
        return secret;
    }
}
