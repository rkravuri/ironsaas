using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Jobs.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>The runner on PostgreSQL (TF-120, TF-122, TF-123, ADR-0061); each provider is one worker process.</summary>
[Collection(JobsCollection.Name)]
public class RunnerTests(JobsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-120 AC1", "ADR-0061")]
    public async Task TwoWorkersRunAJobScheduledEveryMinuteOncePerTenantPerInterval()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var first = tenant.Build(time);
        await using var second = tenant.Build(time);
        Assert.NotEqual(JobsTenant.Runner(first).Holder, JobsTenant.Runner(second).Holder);

        // Six occurrences, each with both workers ticking at once, twice, as their loops would.
        for (var minute = 0; minute < 6; minute++)
        {
            for (var tick = 0; tick < 2; tick++)
            {
                await Task.WhenAll(JobsTenant.Runner(first).RunOnceAsync(Cancel), JobsTenant.Runner(second).RunOnceAsync(Cancel));
            }

            time.Advance(TimeSpan.FromMinutes(1));
        }

        var runs = await JobsTenant.RunsAsync(first, ProbeJobsModule.EveryMinute.Name);
        Assert.Equal(6, runs.Count);
        Assert.Equal(6, runs.Select(r => r.ScheduledFor).Distinct().Count());
        Assert.All(runs, r => Assert.Equal((RunStates.Succeeded, 1), (r.Status, r.Attempts)));
        Assert.Equal(6, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(runs.Select(r => r.Key).Order(), tenant.Behaviour.Runs.Where(r => r.Job == ProbeJobsModule.EveryMinute.Name).Select(r => r.Key).Order());
    }

    [Fact]
    [Proves("TF-120 AC2", "ADR-0061")]
    public async Task ARunPastItsTimeoutIsCancelledLoggedAndRetriedByItsPolicy()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        var id = await JobsTenant.StartAsync(worker, ProbeJobsModule.Slow, "slow-1");

        await JobsTenant.Runner(worker).RunOnceAsync(Cancel);
        var retried = await JobsTenant.RunAsync(worker, id);
        Assert.Equal((RunStates.Queued, 1), (retried.Status, retried.Attempts));
        Assert.StartsWith("Timed out after 300ms", retried.Error, StringComparison.Ordinal);
        Assert.Contains("started, waiting", retried.Log, StringComparison.Ordinal);
        Assert.Contains("Timed out after 300ms: the run was cancelled.", retried.Log, StringComparison.Ordinal);
        Assert.True(retried.DueAt > time.GetUtcNow());

        // Its policy allows two attempts: the second, past its timeout too, makes it a dead letter.
        time.Advance(TimeSpan.FromSeconds(2));
        await JobsTenant.Runner(worker).RunOnceAsync(Cancel);
        var dead = await JobsTenant.RunAsync(worker, id);
        Assert.Equal((RunStates.Dead, 2), (dead.Status, dead.Attempts));
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.RunDeadLettered, ProbeJobsModule.Slow.Name));
    }

    [Fact]
    [Proves("TF-120 AC3", "ADR-0061")]
    public async Task ARunRepeatedByARetryHasTheEffectOfOneRun()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        var id = await JobsTenant.StartAsync(worker, ProbeJobsModule.Idempotent, "order-7");
        Assert.Equal(id, await JobsTenant.StartAsync(worker, ProbeJobsModule.Idempotent, "order-7"));

        await JobsTenant.Runner(worker).RunOnceAsync(Cancel);
        time.Advance(TimeSpan.FromSeconds(5));
        await JobsTenant.DrainAsync(worker);

        Assert.Equal((RunStates.Succeeded, 2), (await JobsTenant.RunAsync(worker, id)) is var run ? (run.Status, run.Attempts) : default);
        Assert.Equal(2, tenant.Behaviour.Count(ProbeJobsModule.Idempotent.Name));
        Assert.Equal(["order-7"], tenant.Behaviour.Applied.Keys);
        Assert.Single(await JobsTenant.RunsAsync(worker, ProbeJobsModule.Idempotent.Name));
    }

    [Fact]
    [Proves("TF-123 AC1", "TF-123 AC2", "ADR-0061")]
    public async Task AFailingRunWaitsLongerBeforeEachRetryThenIsADeadLetterWithItsErrorAndAnAuditEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        var id = await JobsTenant.StartAsync(worker, ProbeJobsModule.Flaky, "flaky-1");

        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await JobsTenant.Runner(worker).RunOnceAsync(Cancel);
            var run = await JobsTenant.RunAsync(worker, id);
            if (run.Status == RunStates.Queued)
            {
                waits.Add(run.DueAt - time.GetUtcNow());
                time.Advance(run.DueAt - time.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            }
        }

        // 10 s, 20 s and 40 s, each moved by up to a fifth (TF-123 AC1).
        Assert.Equal(3, waits.Count);
        Assert.All(waits.Zip([10, 20, 40]), pair => Assert.InRange(pair.First.TotalSeconds, pair.Second * 0.8, pair.Second * 1.2));
        Assert.True(waits[0] < waits[1] && waits[1] < waits[2]);

        var dead = await JobsTenant.RunAsync(worker, id);
        Assert.Equal((RunStates.Dead, 4), (dead.Status, dead.Attempts));
        Assert.Equal("probe.flaky.failed: The flaky probe failed attempt 4.", dead.Error);
        Assert.NotNull(dead.DeadAt);
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.RunDeadLettered, ProbeJobsModule.Flaky.Name));
    }

    [Fact]
    [Proves("TF-123 AC1")]
    public void TheWaitDoublesFromTheFirstDelayUpToTheLongestAndJitterMovesItByAFifthAtMost()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));

        Assert.Equal([10, 20, 40, 60, 60], [.. Enumerable.Range(1, 5).Select(n => policy.DelayAfter(n, 0).TotalSeconds)]);
        Assert.Equal(TimeSpan.FromSeconds(8), policy.DelayAfter(1, -1));
        Assert.Equal(TimeSpan.FromSeconds(12), policy.DelayAfter(1, 1));
        Assert.All(Enumerable.Range(0, 200).Select(_ => RetryPolicy.Jitter()), j => Assert.InRange(j, -1, 1));
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task ARunWhoseWorkerStoppedIsTakenBackByAnotherWorkerWhenItsLeasePassesAndRunsOnce()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var survivor = tenant.Build(time);
        var id = await JobsTenant.StartAsync(survivor, ProbeJobsModule.Slow, "abandoned");

        // A worker took the run and died before it finished: the run stays running under a holder that is gone, with its lease.
        const string crashed = "gone-host/4711/0badc0de";
        await using (var scope = survivor.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            var run = await context.Runs.SingleAsync(r => r.Id == id, Cancel);
            run.Claim(crashed, time.GetUtcNow(), time.GetUtcNow() + ProbeJobsModule.Slow.Timeout + TimeSpan.FromSeconds(30));
            await context.SaveChangesAsync(Cancel);
        }

        var held = await JobsTenant.RunAsync(survivor, id);
        Assert.Equal((RunStates.Running, crashed), (held.Status, held.Holder));

        // Before its lease passes the run is the first worker's; after, the second takes it back.
        await JobsTenant.Runner(survivor).TickAsync(Cancel);
        Assert.Equal(RunStates.Running, (await JobsTenant.RunAsync(survivor, id)).Status);
        time.Advance(TimeSpan.FromSeconds(31));
        var tick = await JobsTenant.Runner(survivor).TickAsync(Cancel);
        Assert.Equal(1, tick.Recovered);
        var recovered = await JobsTenant.RunAsync(survivor, id);
        Assert.Equal((RunStates.Queued, 1, "The worker running it stopped before it finished."), (recovered.Status, recovered.Attempts, recovered.Error));
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task AWorkerThatStopsGivesItsRunsBackAtOnceUncountedAndTheNextWorkerTakesThemWithoutWaitingForALease()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var stopping = tenant.Build(time);
        await using var next = tenant.Build(time);
        var id = await JobsTenant.StartAsync(stopping, ProbeJobsModule.Slow, "interrupted");

        // The first worker takes the run, and its process stops, as at an update, before the run finishes.
        using (var stop = new CancellationTokenSource())
        {
            await JobsTenant.Runner(stopping).TickAsync(stop.Token);
            await stop.CancelAsync();
            await JobsTenant.Runner(stopping).DrainAsync();
        }

        var givenBack = await JobsTenant.RunAsync(next, id);
        var tick = await JobsTenant.Runner(next).TickAsync(Cancel);
        var taken = await JobsTenant.RunAsync(next, id);

        Assert.Equal((RunStates.Queued, 0, null, null), (givenBack.Status, givenBack.Attempts, givenBack.Holder, givenBack.LeaseUntil));
        Assert.Contains("started, waiting", givenBack.Log, StringComparison.Ordinal);
        Assert.Equal((0, RunStates.Running, 1, JobsTenant.Runner(next).Holder), (tick.Recovered, taken.Status, taken.Attempts, taken.Holder));
    }

    [Fact]
    [Proves("TF-122 AC2", "ADR-0061")]
    public async Task InteractiveWorkRunsBeforeBulkWorkThatWasQueuedEarlier()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Configuration["Jobs:Runner:Slots"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        await JobsTenant.StartAsync(worker, ProbeJobsModule.Bulk, "bulk-1");
        await JobsTenant.StartAsync(worker, ProbeJobsModule.Bulk, "bulk-2");
        time.Advance(TimeSpan.FromSeconds(1));
        await JobsTenant.StartAsync(worker, ProbeJobsModule.Interactive, "interactive-1");

        await JobsTenant.DrainAsync(worker);

        Assert.Equal([ProbeJobsModule.Interactive.Name, ProbeJobsModule.Bulk.Name, ProbeJobsModule.Bulk.Name], tenant.Behaviour.Started);
    }

    [Fact]
    [Proves("TF-122 AC4")]
    public void AJobOfAPriorityClassNotDefinedIsRefusedWhereItIsDeclared()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => JobDefinition.Create<CountingJob>(
            "probe.unclassed", "Has no class.", "Probe owners", JobSchedule.Explicit, (PriorityClass)7, TimeSpan.FromSeconds(30)));

        Assert.Contains("priority class that is not defined", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-122 AC3", "ADR-0061")]
    public async Task ASuspendedTenantsDueJobsAreSkippedAndLoggedAndItsQueuedRunsWait()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        var waiting = await JobsTenant.StartAsync(worker, ProbeJobsModule.Explicit, "while-suspended");
        tenant.Standing.Suspended = true;

        var tick = await JobsTenant.Runner(worker).RunOnceAsync(Cancel);

        Assert.True(tick.Skipped >= 1);
        var skipped = Assert.Single(await JobsTenant.RunsAsync(worker, ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(RunStates.Skipped, skipped.Status);
        Assert.Contains("the tenant is suspended", skipped.Log, StringComparison.Ordinal);
        Assert.Equal(RunStates.Queued, (await JobsTenant.RunAsync(worker, waiting)).Status);
        Assert.Equal(0, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name) + tenant.Behaviour.Count(ProbeJobsModule.Explicit.Name));

        // Active again: the next occurrence runs, and the waiting run with it.
        tenant.Standing.Suspended = false;
        time.Advance(TimeSpan.FromMinutes(1));
        await JobsTenant.DrainAsync(worker);
        Assert.Equal(1, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(RunStates.Succeeded, (await JobsTenant.RunAsync(worker, waiting)).Status);
    }

    [Fact]
    [Proves("TF-123 AC3", "ADR-0061")]
    public async Task AJobLateByMoreThanOneIntervalRunsItsLatestOccurrenceAndIsAuditedAsAMissedSchedule()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        await JobsTenant.DrainAsync(worker);
        Assert.Single(await JobsTenant.RunsAsync(worker, ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(0, await tenant.AuditCountAsync(JobsAuditEvents.ScheduleMissed, ProbeJobsModule.EveryMinute.Name));

        // No worker ran for five minutes: one run, of the latest occurrence, and the missed ones counted.
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10));
        var tick = await JobsTenant.Runner(worker).RunOnceAsync(Cancel);

        Assert.Equal(1, tick.Missed);
        var runs = await JobsTenant.RunsAsync(worker, ProbeJobsModule.EveryMinute.Name);
        Assert.Equal(2, runs.Count);
        Assert.Equal(runs[0].ScheduledFor + TimeSpan.FromMinutes(5), runs[1].ScheduledFor);
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.ScheduleMissed, ProbeJobsModule.EveryMinute.Name));
        Assert.Equal("4", await tenant.AuditDetailAsync(JobsAuditEvents.ScheduleMissed, ProbeJobsModule.EveryMinute.Name, "missed"));

        // On time again: no further alert.
        time.Advance(TimeSpan.FromMinutes(1));
        await JobsTenant.DrainAsync(worker);
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.ScheduleMissed, ProbeJobsModule.EveryMinute.Name));
    }

    [Fact]
    [Proves("ADR-0061", "ADR-0059")]
    public async Task ARemovedSuitesQueuedRunsAreHeldAndResumeWhenTheSuiteReturns()
    {
        var tenant = await fixture.NewTenantAsync(withSuite: true);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Guid id;
        await using (var withSuite = tenant.Build(time))
        {
            id = await JobsTenant.StartAsync(withSuite, ProbeSuiteJobsModule.Tally, "tally-1");
        }

        tenant.RunsProbeSuite = false;
        await using (var without = tenant.Build(time))
        {
            var ticks = await JobsTenant.DrainAsync(without);
            Assert.Equal(1, ticks[0].Held);
            var held = await JobsTenant.RunAsync(without, id);
            Assert.Equal((RunStates.Held, 0), (held.Status, held.Attempts));
            Assert.NotNull(held.HeldAt);
            Assert.Equal(0, await tenant.AuditCountAsync(JobsAuditEvents.RunDeadLettered, ProbeSuiteJobsModule.Tally.Name));
            await using var scope = without.CreateAsyncScope();
            var listed = (await scope.ServiceProvider.GetRequiredService<JobConsole>().ListAsync(Cancel)).Single(j => j.Name == ProbeSuiteJobsModule.Tally.Name);
            Assert.Equal((JobStates.Held, 1), (listed.State, listed.Queued));
        }

        tenant.RunsProbeSuite = true;
        await using (var back = tenant.Build(time))
        {
            var ticks = await JobsTenant.DrainAsync(back);
            Assert.Equal(1, ticks[0].Resumed);
            Assert.Equal((RunStates.Succeeded, 1), (await JobsTenant.RunAsync(back, id)) is var run ? (run.Status, run.Attempts) : default);
            Assert.Equal(1, tenant.Behaviour.Count(ProbeSuiteJobsModule.Tally.Name));
        }
    }

    [Fact]
    [Proves("TF-124 AC1", "ADR-0061")]
    public async Task ANewTenantsSeedMakesEveryDeclaredJobActiveAndScheduled()
    {
        var tenant = await fixture.NewTenantAsync(holdOtherModules: false);
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var declarations = provider.GetRequiredService<Iron.Kernel.Api.Modules.JobDeclarations>();

        foreach (var job in declarations.UnitJobs)
        {
            var record = await JobsTenant.RecordAsync(provider, job.Name);
            Assert.False(record.Paused, job.Name);
            Assert.Equal(job.IsScheduled, record.NextDueAt.HasValue);
        }

        // The maintenance jobs the worker's loops became are among them (TF-124 AC1).
        Assert.Contains(declarations.UnitJobs, j => j.Name == PruneRunsJob.Definition.Name);
        Assert.Contains(declarations.UnitJobs, j => j.Name == "audit.seal_chain");
        Assert.Contains(declarations.UnitJobs, j => j.Name == "audit.retention");
        Assert.Contains(declarations.UnitJobs, j => j.Name == "events.dispatch");
        Assert.Contains(declarations.UnitJobs, j => j.Name == "events.archive");
        Assert.Contains(declarations.UnitJobs, j => j.Name == "authz.escalate_reviews");
    }
}
