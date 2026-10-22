using System.Security.Cryptography;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// Step 4: the unit's secret (<c>tenants/&lt;slug&gt;/unit</c>): the app connection string, the app role, the signed container
/// URL, a signing key and the key that encrypts the second factors' secrets (ADR-0047), each key generated once. Then a policy that reads that path and the tenant's entitlements
/// (<c>tenants/&lt;slug&gt;/entitlements</c>, ADR-0034) and its flag rules (<c>tenants/&lt;slug&gt;/flags</c> and the shared
/// <c>platform/flags</c>, ADR-0035) and nothing else, and one unit token with it, kept at
/// <c>tenants/&lt;slug&gt;/fleet</c> for the unit's environment (TF-073, TF-210).
/// An update runs it too (ADR-0028): the unit secret is written from what the two steps before it hold now, the policy
/// is written again, so a unit made before a path was added reads it with its old token, and a lost unit token means a
/// new token, which the unit step then puts into the unit's environment. The entitlements are written again as well,
/// so a store that lost them heals with the unit.
/// </summary>
public sealed class SecretsStep(ISecretStore secrets, ISecretStoreAdmin admin, ITenantEntitlements entitlements, ITenantFlags flags) : IProvisioningStep
{
    public const string UnitTokenKey = "unit_token";

    /// <summary>The configuration key of the second factors' key (TF-073, ADR-0047): 32 random bytes in base64.</summary>
    public const string FactorKeyKey = "Identity:FactorKey";

    public string Name => StepNames.Secrets;

    public int Order => 4;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The database step has not run: no database secret.");
        var storage = await secrets.ReadAsync(context.StorageSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The storage step has not run: no storage secret.");
        var current = await secrets.ReadAsync(context.UnitSecretPath, cancellationToken).ConfigureAwait(false);

        var unit = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Database"] = context.ConnectionString(context.AppRole, database[DatabaseStep.AppPasswordKey]),
            ["Database:AppRole"] = context.AppRole,
            ["Storage:ContainerSasUrl"] = storage[StorageStep.ContainerSasUrlKey],
            ["Tokens:SigningKeyPem"] = current?.GetValueOrDefault("Tokens:SigningKeyPem") ?? NewSigningKey(),

            // Kept as it is once made: a new key would leave every authenticator app's secret unreadable.
            [FactorKeyKey] = current?.GetValueOrDefault(FactorKeyKey) ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        await secrets.WriteAsync(context.UnitSecretPath, unit, cancellationToken).ConfigureAwait(false);

        // On every run: the token carries the policy by name, so writing it again widens what an existing token may read.
        await admin.CreateReadPolicyAsync(context.UnitPolicy, [context.UnitSecretPath, context.EntitlementsSecretPath, context.FlagsSecretPath, ProvisioningContext.PlatformFlagsPath, context.SettingsSecretPaths], cancellationToken).ConfigureAwait(false);

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
        return $"unit secret written with {unit.Count} keys; policy {context.UnitPolicy} reads the unit secret, the entitlements, the flag rules and the settings' secrets; unit token {(tokenCreated ? "created" : "kept")}; entitlements {(published ? "written" : "left to the reconcile")}; flag rules {(flagsPublished ? "written" : "left to the reconcile")}";
    }

    private static string NewSigningKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportECPrivateKeyPem();
    }
}
