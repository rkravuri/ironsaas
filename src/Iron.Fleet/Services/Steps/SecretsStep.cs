using System.Security.Cryptography;
using Iron.Fleet.Contracts;
using Iron.Security.Secrets;

namespace Iron.Fleet.Services.Steps;

/// <summary>
/// Step 4: the unit's secret (<c>tenants/&lt;slug&gt;/unit</c>): the app connection string, the app role, the signed container
/// URL and a signing key, generated once. Then a policy that reads that path and nothing else, and one unit token with
/// it, kept at <c>tenants/&lt;slug&gt;/fleet</c> for the unit's environment (TF-073, TF-210).
/// </summary>
public sealed class SecretsStep(ISecretStore secrets, ISecretStoreAdmin admin) : IProvisioningStep
{
    public const string UnitTokenKey = "unit_token";

    public string Name => StepNames.Secrets;

    public int Order => 4;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision];

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
        };
        await secrets.WriteAsync(context.UnitSecretPath, unit, cancellationToken).ConfigureAwait(false);

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false);
        var tokenCreated = false;
        if (fleet?.GetValueOrDefault(UnitTokenKey) is null)
        {
            await admin.CreateReadPolicyAsync(context.UnitPolicy, [context.UnitSecretPath], cancellationToken).ConfigureAwait(false);
            var token = await admin.CreateTokenAsync([context.UnitPolicy], $"unit-{context.Slug}", TimeSpan.FromDays(context.Options.Secrets.UnitTokenValidDays), cancellationToken).ConfigureAwait(false);
            await secrets.WriteAsync(context.FleetSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { [UnitTokenKey] = token }, cancellationToken).ConfigureAwait(false);
            tokenCreated = true;
        }

        return $"unit secret written with {unit.Count} keys; policy {context.UnitPolicy}; unit token {(tokenCreated ? "created" : "kept")}";
    }

    private static string NewSigningKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportECPrivateKeyPem();
    }
}
