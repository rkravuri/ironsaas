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

/// <summary>
/// The operator's search or replay of a tenant's domain events (TF-112, ADR-0060): the image's <c>events</c> command in
/// the unit, as the owner role, with what the control plane asked on the job. Its answer, identifiers and states and never
/// an event's data, is the step's output; a refusal fails the step with the command's reason.
/// </summary>
public sealed class EventsStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : UnitAnswerStep(runtime, secrets, fleetStore)
{
    public override string Name => StepNames.Events;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.Events];
}

/// <summary>
/// The operator's list, start, pause, resume and re-run of a tenant's jobs (TF-121, ADR-0061): the image's <c>jobs</c>
/// command in the unit, as the owner role. Its answer, the jobs' states and runs and never a run's log or payload, is the
/// step's output; the unit's trail records each action in the operator's name with the reason.
/// </summary>
public sealed class JobsStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : UnitAnswerStep(runtime, secrets, fleetStore)
{
    public override string Name => StepNames.Jobs;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.Jobs];
}

/// <summary>
/// A unit command whose answer is the step's output (ADR-0060, ADR-0061): the command is named as the step is, runs as the
/// owner role with what the control plane put on the job, and fails the step with the command's own reason.
/// </summary>
public abstract class UnitAnswerStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    /// <summary>The longest answer the step keeps, its end, which holds the command's count: what a step's report holds.</summary>
    public const int OutputMaxLength = Data.ProvisioningStep.OutputMaxLength;

    public abstract string Name { get; }

    public int Order => 1;

    public abstract IReadOnlyCollection<JobKind> Kinds { get; }

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request ?? throw new InvalidOperationException($"A {Name} job carries what the operator asked; this one has nothing.");
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        UnitEnvironment.AddHostnames(environment, context.Tenant.Hostnames);
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The tenant's database secret is missing.");
        environment["ConnectionStrings__Database"] = context.ConnectionString(context.OwnerRole, database[DatabaseStep.OwnerPasswordKey]);
        foreach (var (key, value) in request.Environment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        // The command's answer is its output, so the host's own information lines stay out of it; warnings still show.
        environment["Logging__LogLevel__Default"] = "Warning";
        var result = await runtime.RunCommandAsync(context.ToUnit(environment), [Name], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{Name} exited with {result.ExitCode}: {MigrateStep.Tail(result.Output)}");
        }

        var answer = result.Output.Trim();
        return answer.Length <= OutputMaxLength ? answer : "…" + answer[^(OutputMaxLength - 1)..];
    }
}

/// <summary>The break-glass account ends, and its review of every action waits in the store for the control plane (TF-026 AC3).</summary>
public sealed class BreakGlassCloseStep(IUnitRuntime runtime, ISecretStore secrets, ISecretStoreAdmin admin, SecretStoreOptions fleetStore) : OperatorCommandStep(runtime, secrets, admin, fleetStore)
{
    public override string Name => StepNames.BreakGlassClose;

    public override IReadOnlyCollection<JobKind> Kinds => [JobKind.BreakGlassClose];

    protected override string Command => "break-glass-close";

    protected override string Reported(JobRequest request) => $"break-glass account {request.Subject} closed; its review waits in the store for the control plane";
}
