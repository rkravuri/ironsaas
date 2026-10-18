using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Iron.Fleet.Contracts;
using Iron.Security.Secrets;

namespace Iron.Fleet.Services.Steps;

/// <summary>The environment the image commands and the unit get: the store address, the unit token and the path (TF-073). Nothing else.</summary>
public static class UnitEnvironment
{
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

/// <summary>Step 5: the image's <c>migrate</c> command as the owner role; migrations and the app role's grants.</summary>
public sealed class MigrateStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.Migrate;

    public int Order => 5;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision];

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
/// Step 6: the image's <c>seed</c> command as the owner role: system roles, the first administrator (Invited, with an
/// invitation token), the smoke client, the container. The invitation link is read from the output and reported.
/// </summary>
public sealed partial class SeedStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public const string SmokeClientId = "smoke-client";
    public const string SmokeSecretKey = "smoke_client_secret";
    public const string InvitationLinkKey = "invitation_link";

    public string Name => StepNames.Seed;

    public int Order => 6;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision];

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
        environment["Seed__ServiceAccounts__0__ClientId"] = SmokeClientId;
        environment["Seed__ServiceAccounts__0__Secret"] = smokeSecret;
        environment["Seed__ServiceAccounts__0__Role"] = "service-default";

        var result = await runtime.RunCommandAsync(context.ToUnit(environment), ["seed"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"seed exited with {result.ExitCode}: {MigrateStep.Tail(result.Output)}");
        }

        var match = InvitationToken().Match(result.Output);
        if (match.Success)
        {
            var link = $"https://{context.Tenant.Hostnames[0]}/invitations/{match.Groups["token"].Value}";
            var latest = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var updated = new Dictionary<string, string>(latest, StringComparer.Ordinal) { [InvitationLinkKey] = link };
            await secrets.WriteAsync(context.FleetSecretPath, updated, cancellationToken).ConfigureAwait(false);
            return $"administrator {context.Tenant.AdministratorLoginId} invited: {link}";
        }

        return "seed completed (the administrator existed already; the earlier invitation stands)";
    }

    [GeneratedRegex("Invitation token: (?<token>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex InvitationToken();
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

/// <summary>Step 8: the unit answers its readiness probe through the ingress; the tenant becomes Active; the invitation link is the job's output.</summary>
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

        var fleet = await secrets.ReadAsync(context.FleetSecretPath, cancellationToken).ConfigureAwait(false);
        var link = fleet?.GetValueOrDefault(SeedStep.InvitationLinkKey);
        return link is null ? $"{context.Tenant.Hostnames[0]} is ready" : $"{context.Tenant.Hostnames[0]} is ready; invitation: {link}";
    }
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
