using System.Text.RegularExpressions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// The one step of an operator's support or emergency access (TF-025, TF-026, ADR-0049): the image's command in the unit,
/// as the owner role like the recovery, with the configuration the control plane put on the job and the job's one-time
/// handover place. The command hands its link, or the review, over through the store; the step's output says so and
/// holds neither. A refusal fails the step with the command's reason. A new request is a new job.
/// </summary>
public abstract partial class OperatorCommandStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public abstract string Name { get; }

    public int Order => 1;

    public abstract IReadOnlyCollection<JobKind> Kinds { get; }

    /// <summary>The image's command.</summary>
    protected abstract string Command { get; }

    /// <summary>What the step reports when the command handed its item over.</summary>
    protected abstract string Reported(JobRequest request);

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request ?? throw new InvalidOperationException($"A {Command} job carries what the command needs; this one has nothing.");
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The tenant's database secret is missing.");
        environment["ConnectionStrings__Database"] = context.ConnectionString(context.OwnerRole, database[DatabaseStep.OwnerPasswordKey]);
        foreach (var (key, value) in request.Environment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        await UnitEnvironment.AddHandoverAsync(environment, context, secrets, admin, cancellationToken).ConfigureAwait(false);
        var result = await runtime.RunCommandAsync(context.ToUnit(environment), [Command], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !HandedOver().IsMatch(result.Output))
        {
            throw new InvalidOperationException($"{Command} exited with {result.ExitCode}: {MigrateStep.Tail(result.Output)}");
        }

        return Reported(request);
    }

    [GeneratedRegex("Handed over (an impersonation link|a break-glass sign-in link|a break-glass review)", RegexOptions.CultureInvariant)]
    private static partial Regex HandedOver();
}

/// <summary>The operator's link to sign in as a person, where the tenant allows it (TF-025, ADR-0049).</summary>
public sealed class ImpersonateStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : OperatorCommandStep(runtime, secrets, admin, fleetStore)
{
    public override string Name => StepNames.Impersonate;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.Impersonate];

    protected override string Command => "impersonate";

    protected override string Reported(JobRequest request) => $"impersonation link for {request.Subject} handed over: it waits in the store for the operator who asked, once, for 15 minutes";
}

/// <summary>The break-glass account and its one-time link for the operator who asked (TF-026 AC1, ADR-0049).</summary>
public sealed class BreakGlassOpenStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : OperatorCommandStep(runtime, secrets, admin, fleetStore)
{
    public override string Name => StepNames.BreakGlassOpen;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.BreakGlassOpen];

    protected override string Command => "break-glass-open";

    protected override string Reported(JobRequest request) => $"break-glass account {request.Subject} opened; its sign-in link waits in the store for the operator who asked, once";
}

/// <summary>The break-glass account ends, and its review of every action waits in the store for the control plane (TF-026 AC3).</summary>
public sealed class BreakGlassCloseStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : OperatorCommandStep(runtime, secrets, admin, fleetStore)
{
    public override string Name => StepNames.BreakGlassClose;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.BreakGlassClose];

    protected override string Command => "break-glass-close";

    protected override string Reported(JobRequest request) => $"break-glass account {request.Subject} closed; its review waits in the store for the control plane";
}
