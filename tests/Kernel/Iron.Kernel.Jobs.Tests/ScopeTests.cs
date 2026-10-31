using System.Diagnostics;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Jobs.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>A run's trace and tenant (TF-130 AC3, TF-003 AC2), and the jobs every process runs for itself (ADR-0061).</summary>
[Collection(JobsCollection.Name)]
public class ScopeTests(JobsFixture fixture)
{
    private static readonly ActivitySource Requests = new("Iron.Tests.Requests");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-130 AC3", "TF-003 AC2", "ADR-0061")]
    public async Task AJobAStartedRequestStartedLinksToItsTraceQueriesItsTenantsDatabaseOnlyAndCarriesItsTenant()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var worker = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is JobsTelemetry.SourceName or "Iron.Tests.Requests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        // A request starts the job; its span ends before the worker runs it, as a request's does.
        ActivityTraceId origin;
        using (var request = Requests.StartActivity("POST /api/v1/things", ActivityKind.Server))
        {
            Assert.NotNull(request);
            origin = request.TraceId;
            await JobsTenant.StartAsync(worker, ProbeJobsModule.Scope, "scope-1");
        }

        Assert.Null(Activity.Current);
        await JobsTenant.Runner(worker).RunOnceAsync(Cancel);

        var (database, tenantId, links) = tenant.Behaviour.Seen ?? throw new InvalidOperationException("The scope job did not run.");
        Assert.Equal(tenant.Database, database);
        Assert.Equal("acme", tenantId);
        Assert.Equal([origin], links);
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task AJobEachProcessRunsForItselfRunsOnItsIntervalInEveryProcessAndIsNeitherPausedNorStarted()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var first = tenant.Build(time);
        await using var second = tenant.Build(time);
        var runners = new[] { first, second }.Select(p => p.GetServices<IHostedService>().OfType<ProcessJobRunner>().Single()).ToList();
        foreach (var runner in runners)
        {
            await runner.StartAsync(Cancel);
        }

        // Each process runs it as it starts, then every minute from then, whenever its loop gets to wait.
        await WaitForAsync(() => tenant.Behaviour.Count(ProbeJobsModule.EachProcess.Name) >= 2);
        for (var minute = 1; minute <= 3; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            var expected = 2 * (minute + 1);
            await WaitForAsync(() => tenant.Behaviour.Count(ProbeJobsModule.EachProcess.Name) >= expected);
        }

        foreach (var runner in runners)
        {
            await runner.StopAsync(Cancel);
        }

        // Each process ran it each minute; the database knows nothing of it.
        Assert.Equal(8, tenant.Behaviour.Count(ProbeJobsModule.EachProcess.Name));
        Assert.Empty(await JobsTenant.RunsAsync(first, ProbeJobsModule.EachProcess.Name));
        Assert.Equal(JobScope.EachProcess, first.GetRequiredService<JobDeclarations>().Find(ProbeJobsModule.EachProcess.Name)!.Definition.Scope);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Cancel);
        }

        Assert.True(condition());
    }
}
