using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// The environment the image commands and the unit get: the store address, the unit token and the path (TF-073), and
/// the tenant's suites (TF-244, ADR-0059). Nothing else.
/// </summary>
public static class UnitEnvironment
{
    /// <summary>
    /// The variable that names the tenant's suites, which the hosts read as <c>Suites:Installed</c> (ADR-0059). It is in
    /// the unit's environment, not its secret, so a change of the tenant's suites changes the unit's containers and the
    /// update recreates them: a unit runs exactly the suites the control plane records.
    /// </summary>
    public const string SuitesVariable = "Suites__Installed";

    /// <summary>How long a command's handover token lives: long enough for the command, too short to matter afterwards.</summary>
    public static readonly TimeSpan HandoverTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gives an image command the job's one-time place (ADR-0046): the place emptied first, so a step run again writes
    /// afresh, and a token that may write the tenant's handover places and nothing else, not even read them, which ends
    /// with the command. The unit's own token stays read-only.
    /// </summary>
    public static async Task AddHandoverAsync(Dictionary<string, string> environment, ProvisioningContext context, ISecretStore secrets, ISecretStoreAdmin admin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(admin);
        await secrets.DeleteAsync(context.HandoverPath, cancellationToken).ConfigureAwait(false);
        await admin.CreateDepositPolicyAsync(context.HandoverPolicy, HandoverPlaces.PatternOf(context.Slug), cancellationToken).ConfigureAwait(false);
        environment["Handover__Path"] = context.HandoverPath;
        environment["Handover__Token"] = await admin.CreateTokenAsync([context.HandoverPolicy], $"handover {context.Slug} {context.JobId:N}", HandoverTokenLifetime, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Dictionary<string, string>> ForUnitAsync(ProvisioningContext context, ISecretStore secrets, SecretStoreOptions fleetStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(fleetStore);
        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The secrets step has not run: no unit token.");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tenant__Id"] = context.Slug,
            ["Secrets__Address"] = context.Options.Secrets.UnitAddress ?? fleetStore.Address ?? throw new InvalidOperationException("Fleet:Secrets:UnitAddress is required."),
            ["Secrets__Token"] = fleet[SecretsStep.UnitTokenKey],
            ["Secrets__Mount"] = fleetStore.Mount,
            ["Secrets__Path"] = $"tenants/{context.Slug}",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = context.Options.Units.OtlpEndpoint,
            [SuitesVariable] = string.Join(',', context.Tenant.InstalledSuites),
        };
    }

    public static void AddHostnames(Dictionary<string, string> environment, IReadOnlyList<string> hostnames)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(hostnames);
        for (var i = 0; i < hostnames.Count; i++)
        {
            environment[$"Tenant__Hostnames__{i}"] = hostnames[i];
        }
    }
}

/// <summary>Step 5: the image's <c>migrate</c> command as the owner role; migrations and the app role's grants. Runs again on an update: a new image brings new migrations.</summary>
public sealed class MigrateStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.Migrate;

    public int Order => 5;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The database step has not run.");
        environment["ConnectionStrings__Database"] = context.ConnectionString(context.OwnerRole, database[DatabaseStep.OwnerPasswordKey]);

        var result = await runtime.RunCommandAsync(context.ToUnit(environment), ["migrate"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"migrate exited with {result.ExitCode}: {Tail(result.Output)}");
        }

        return Tail(result.Output);
    }

    internal static string Tail(string output) => output.Length > 1500 ? output[^1500..] : output;
}

/// <summary>
/// Step 6: the image's <c>seed</c> command as the owner role: system roles, the root organisation unit (named after the
/// tenant, coded with its slug), the first administrator (Invited, with an invitation token), the smoke client, the
/// container. The invitation link is handed over through the job's place (ADR-0046), never read from the output or
/// kept in the fleet's secret. Runs again on an update: seeds are idempotent, and a new image may bring new permissions
/// for the system roles; an administrator still Invited gets a new invitation, handed over the same way.
/// </summary>
public sealed partial class SeedStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public const string SmokeClientId = "smoke-client";
    public const string SmokeSecretKey = "smoke_client_secret";

    public string Name => StepNames.Seed;

    public int Order => 6;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The database step has not run.");
        environment["ConnectionStrings__Database"] = context.ConnectionString(context.OwnerRole, database[DatabaseStep.OwnerPasswordKey]);

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var smokeSecret = fleet.GetValueOrDefault(SmokeSecretKey) ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        if (!fleet.ContainsKey(SmokeSecretKey))
        {
            var updated = new Dictionary<string, string>(fleet, StringComparer.Ordinal) { [SmokeSecretKey] = smokeSecret };
            await secrets.WriteAsync(context.FleetSecretPath, updated, cancellationToken).ConfigureAwait(false);
        }

        environment["Seed__Administrator__LoginId"] = context.Tenant.AdministratorLoginId;
        environment["Seed__Administrator__DisplayName"] = context.Tenant.AdministratorDisplayName ?? "Administrator";
        environment["Seed__RootUnit__Name"] = context.Tenant.DisplayName;
        environment["Seed__RootUnit__Code"] = context.Slug;
        environment["Seed__ServiceAccounts__0__ClientId"] = SmokeClientId;
        environment["Seed__ServiceAccounts__0__Secret"] = smokeSecret;
        environment["Seed__ServiceAccounts__0__Role"] = "service-default";

        // The job that seeds, which a suite's seed records with its installation (TF-244, ADR-0059).
        environment["Seed__JobId"] = context.JobId.ToString("D");
        await UnitEnvironment.AddHandoverAsync(environment, context, secrets, admin, cancellationToken).ConfigureAwait(false);

        var result = await runtime.RunCommandAsync(context.ToUnit(environment), ["seed"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"seed exited with {result.ExitCode}: {MigrateStep.Tail(result.Output)}");
        }

        return HandedOver().IsMatch(result.Output)
            ? $"administrator {context.Tenant.AdministratorLoginId} invited; the invitation waits in the store for the operator who asked, once"
            : "seed completed (the administrator existed already)";
    }

    [GeneratedRegex("Handed over an invitation", RegexOptions.CultureInvariant)]
    private static partial Regex HandedOver();
}

/// <summary>Step 7: the unit itself: containers and routes, from the template (T14b renders it).</summary>
public sealed class UnitStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.Unit;

    public int Order => 7;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        await runtime.ApplyAsync(context.ToUnit(environment), cancellationToken).ConfigureAwait(false);
        return $"unit {context.Slug} applied with image tag {context.Options.Units.ImageTag} and size {context.Tenant.SizeClass.ToString().ToLowerInvariant()}";
    }
}

/// <summary>Step 8: the unit answers its readiness probe through the ingress; the tenant becomes Active.</summary>
public sealed class VerifyStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore, TimeProvider time) : IProvisioningStep
{
    public string Name => StepNames.Verify;

    public int Order => 8;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        var unit = context.ToUnit(environment);
        var deadline = time.GetUtcNow().AddSeconds(context.Options.Units.ReadyTimeoutSeconds);
        string? problem;
        while (true)
        {
            problem = await runtime.CheckReadyAsync(unit, cancellationToken).ConfigureAwait(false);
            if (problem is null || time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), time, cancellationToken).ConfigureAwait(false);
        }

        if (problem is not null)
        {
            throw new InvalidOperationException($"The unit did not become ready within {context.Options.Units.ReadyTimeoutSeconds} s: {problem}");
        }

        return $"{context.Tenant.Hostnames[0]} is ready";
    }
}

/// <summary>
/// The one step of a recovery job (TF-017, ADR-0046): the image's <c>recover-admin</c> command in the unit, with the
/// administrator, the operator and the reason, as the owner role like the seed. The command checks that the person is an
/// active tenant administrator, records the operator and the reason in the tenant's audit trail, and hands the link over
/// through the job's place in the store; the control plane gives it once to the operator who asked. The step's output
/// says so and holds no link. A refusal fails the step with the command's reason. A new request is a new job.
/// </summary>
public sealed partial class RecoverStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.Recover;

    public int Order => 1;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Recover];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request ?? throw new InvalidOperationException("A recovery job carries its administrator, reason and operator; this one has none.");
        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.RequestedBy))
        {
            throw new InvalidOperationException("A recovery job needs the administrator's login, the reason and the operator.");
        }

        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The tenant's database secret is missing.");
        environment["ConnectionStrings__Database"] = context.ConnectionString(context.OwnerRole, database[DatabaseStep.OwnerPasswordKey]);
        environment["Recovery__LoginId"] = request.Subject;
        environment["Recovery__Operator"] = request.RequestedBy;
        environment["Recovery__Reason"] = request.Reason;
        if (request.ResetFactors)
        {
            environment["Recovery__ResetFactors"] = "true";
        }
        await UnitEnvironment.AddHandoverAsync(environment, context, secrets, admin, cancellationToken).ConfigureAwait(false);

        var result = await runtime.RunCommandAsync(context.ToUnit(environment), ["recover-admin"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !HandedOver().IsMatch(result.Output))
        {
            throw new InvalidOperationException($"recover-admin exited with {result.ExitCode}: {MigrateStep.Tail(result.Output)}");
        }

        var factors = request.ResetFactors ? "; the administrator's second factors were reset" : string.Empty;
        return $"recovery link for {request.Subject} handed over: it waits in the store for the operator who asked, once, for 15 minutes{factors}";
    }

    [GeneratedRegex("Handed over a recovery link", RegexOptions.CultureInvariant)]
    private static partial Regex HandedOver();
}

public static class ProvisioningContextExtensions
{
    public static UnitDefinition ToUnit(this ProvisioningContext context, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new UnitDefinition(context.Tenant, context.Options.Units.ImageTag, SizeClasses.LimitsOf(context.Tenant.SizeClass), environment);
    }
}

/// <summary>The one step of a de-provisioning job (TF-210, S1): the unit's containers and routes go; database, container and secrets stay for retention (S6).</summary>
public sealed class RemoveStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.Remove;

    public int Order => 1;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Deprovision];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        await runtime.RemoveAsync(context.ToUnit(environment), cancellationToken).ConfigureAwait(false);
        return $"unit {context.Slug} removed; database {context.Tenant.DatabaseName}, container {context.Tenant.StorageContainer} and secrets kept";
    }
}
