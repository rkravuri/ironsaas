using System.Collections.Concurrent;
using System.Diagnostics;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Jobs.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>Something happened in the probe that starts a job: an event trigger is a subscription that starts the job, keyed by the event.</summary>
public sealed record ThingHappened(Guid ThingId);

/// <summary>A probe module whose jobs each show one thing the runner does, as a module's jobs would (ADR-0061).</summary>
public sealed class ProbeJobsModule : IModule
{
    public static readonly EventType<ThingHappened> Happened = new("probe.thing.happened", 1, "probe_thing", "A thing happened in the probe.", "Probe owners");

    public static readonly RetryPolicy Quick = new(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

    public static readonly JobDefinition EveryMinute = JobDefinition.Create<CountingJob>(
        "probe.every_minute", "Counts its runs, once a minute.", "Probe owners", JobSchedule.Interval(TimeSpan.FromMinutes(1)), PriorityClass.Normal, TimeSpan.FromSeconds(30), Quick);

    public static readonly JobDefinition Explicit = JobDefinition.Create<CountingJob>(
        "probe.explicit", "Counts its runs, when started.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromSeconds(30), Quick);

    public static readonly JobDefinition Slow = JobDefinition.Create<SlowJob>(
        "probe.slow", "Waits until it is cancelled.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromMilliseconds(300), new RetryPolicy(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

    public static readonly JobDefinition Flaky = JobDefinition.Create<FlakyJob>(
        "probe.flaky", "Fails as the test asks, then succeeds.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromSeconds(30), new RetryPolicy(4, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));

    public static readonly JobDefinition Idempotent = JobDefinition.Create<IdempotentJob>(
        "probe.idempotent", "Applies its effect by its key, then fails its first attempt.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromSeconds(30), Quick, uniqueBy: "the order it applies");

    public static readonly JobDefinition Bulk = JobDefinition.Create<OrderingJob>(
        "probe.bulk", "Records when it starts.", "Probe owners", JobSchedule.Explicit, PriorityClass.Bulk, TimeSpan.FromSeconds(30), Quick, maxParallel: 4);

    public static readonly JobDefinition Interactive = JobDefinition.Create<OrderingJob>(
        "probe.interactive", "Records when it starts.", "Probe owners", JobSchedule.Explicit, PriorityClass.Interactive, TimeSpan.FromSeconds(30), Quick, maxParallel: 4);

    public static readonly JobDefinition Scope = JobDefinition.Create<ScopeJob>(
        "probe.scope", "Records its database and its span's tenant and links.", "Probe owners", JobSchedule.Explicit, PriorityClass.Interactive, TimeSpan.FromSeconds(30), Quick);

    public static readonly JobDefinition FromEvent = JobDefinition.Create<CountingJob>(
        "probe.on_thing", "Counts each thing that happened, started by the event.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromSeconds(30), Quick, uniqueBy: "the event it was started by");

    public static readonly JobDefinition EachProcess = JobDefinition.Create<CountingJob>(
        "probe.each_process", "Counts its runs in this process.", "Probe owners", JobSchedule.Interval(TimeSpan.FromMinutes(1)), PriorityClass.Normal, TimeSpan.FromSeconds(30), Quick, JobScope.EachProcess);

    public string Name => "probe";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<EventType> Events => [Happened];

    public IReadOnlyList<EventSubscription> Subscriptions =>
    [
        EventSubscription.To<ThingHappened, StartOnThing>(Happened, "probe.start_on_thing", "Starts probe.on_thing for each thing that happened."),
    ];

    public IReadOnlyList<JobDefinition> Jobs => [EveryMinute, Explicit, Slow, Flaky, Idempotent, Bulk, Interactive, Scope, FromEvent, EachProcess];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>A probe suite's module with a job: a unit runs it only where the suite is installed, and holds its runs while it is removed.</summary>
public sealed class ProbeSuiteJobsModule : IModule
{
    public const string SuiteShortName = "probesuite";

    public static readonly JobDefinition Tally = JobDefinition.Create<CountingJob>(
        "probesuite.tally", "Counts its runs in the suite.", "Probe owners", JobSchedule.Explicit, PriorityClass.Normal, TimeSpan.FromSeconds(30), ProbeJobsModule.Quick);

    public string Name => "probesuite";

    public string Suite => SuiteShortName;

    public ModuleLicence? Licence { get; } = new("probesuite", SubscriptionType.Platform);

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<JobDefinition> Jobs => [Tally];

    public static SuiteDefinition Definition() => new(SuiteShortName, "Probe suite", "A suite the jobs tests install and remove.", () => [new ProbeSuiteJobsModule()]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>What the probe's jobs did and what a test asks of them.</summary>
public sealed class ProbeJobBehaviour
{
    /// <summary>Each run's key, each time a job ran, by job.</summary>
    public ConcurrentQueue<(string Job, string Key)> Runs { get; } = new();

    /// <summary>How many attempts of the flaky job fail before one succeeds; a negative number, every one.</summary>
    public int FlakyFailures { get; set; }

    public int FlakyAttempts => _flakyAttempts;

    /// <summary>The idempotent job's effect: one entry per key, however often it runs.</summary>
    public ConcurrentDictionary<string, int> Applied { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Started { get; } = new();

    /// <summary>Whether the subscription that starts a job on an event fails, to make an event's dead letter.</summary>
    public bool StartFails { get; set; }

    /// <summary>What the scope job saw: its database, and its span's tenant and links.</summary>
    public (string Database, string? Tenant, ActivityTraceId[] Links)? Seen { get; set; }

    private int _flakyAttempts;

    public int NextFlakyAttempt() => Interlocked.Increment(ref _flakyAttempts);

    public int Count(string job) => Runs.Count(r => r.Job == job);
}

public sealed class CountingJob(ProbeJobBehaviour behaviour) : IJob
{
    public Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        behaviour.Runs.Enqueue((context.Job, context.Key));
        context.Log($"counted {context.Key}");
        return Task.FromResult(Result.Success());
    }
}

public sealed class SlowJob : IJob
{
    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Log("started, waiting");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Result.Success();
    }
}

public sealed class FlakyJob(ProbeJobBehaviour behaviour) : IJob
{
    public Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var attempt = behaviour.NextFlakyAttempt();
        if (behaviour.FlakyFailures < 0 || attempt <= behaviour.FlakyFailures)
        {
            context.Log($"attempt {attempt} fails");
            return Task.FromResult<Result>(Error.Conflict("probe.flaky.failed", $"The flaky probe failed attempt {attempt}."));
        }

        behaviour.Runs.Enqueue((context.Job, context.Key));
        return Task.FromResult(Result.Success());
    }
}

/// <summary>Applies its effect by its run's key, as an upsert would, then fails its first attempt: the retry applies it again, to the same effect.</summary>
public sealed class IdempotentJob(ProbeJobBehaviour behaviour) : IJob
{
    public Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        behaviour.Applied.AddOrUpdate(context.Key, 1, (_, _) => 1);
        behaviour.Runs.Enqueue((context.Job, context.Key));
        return Task.FromResult(context.Attempt == 1 ? (Result)Error.Conflict("probe.idempotent.failed", "The first attempt fails after its effect.") : Result.Success());
    }
}

public sealed class OrderingJob(ProbeJobBehaviour behaviour) : IJob
{
    public Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        behaviour.Started.Enqueue(context.Job);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>Records the database its queries run against and its span's tenant and links (TF-003 AC2, TF-130 AC3).</summary>
public sealed class ScopeJob(JobsDbContext context, ProbeJobBehaviour behaviour) : IJob
{
    public async Task<Result> RunAsync(JobContext job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var database = await context.Database.SqlQuery<string>($"select current_database() as \"Value\"").SingleAsync(cancellationToken);
        var span = Activity.Current;
        behaviour.Seen = (database, span?.GetTagItem("tenant.id") as string, [.. (span?.Links ?? []).Select(l => l.Context.TraceId)]);
        return Result.Success();
    }
}

/// <summary>Starts <c>probe.on_thing</c> for each thing that happened, keyed by the event: a redelivery starts no second run.</summary>
public sealed class StartOnThing(IJobStarter starter, ProbeJobBehaviour behaviour) : IEventHandler<ThingHappened>
{
    public async Task<Result> HandleAsync(DomainEvent<ThingHappened> domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (behaviour.StartFails)
        {
            return Error.Conflict("probe.start.failed", "The probe's subscription failed, as the test asked.");
        }

        await starter.StartAsync(ProbeJobsModule.FromEvent, $"event:{domainEvent.Id:D}", domainEvent.Data.ThingId.ToString("D"), cancellationToken);
        return Result.Success();
    }
}
