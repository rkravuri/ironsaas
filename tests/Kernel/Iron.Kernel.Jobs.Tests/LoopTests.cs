using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>
/// The term job's loop (ADR-0061): its term ends at the run's next occurrence, so a late start never carries into the
/// next run, and its outcome is its last pass's. Each pass moves the clock itself, so no timer is involved.
/// </summary>
public class LoopTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Proves("ADR-0061")]
    public async Task ARunThatStartedLateEndsAtItsNextOccurrenceNotATermAfterItsStart()
    {
        var onTime = await PassesAsync(scheduledFor: Start);
        var late = await PassesAsync(scheduledFor: Start.AddSeconds(-45));
        var pastItsTerm = await PassesAsync(scheduledFor: Start.AddMinutes(-2));

        // Five seconds a pass: a minute from its occurrence is twelve passes; started 45 seconds late, three; past it, one.
        Assert.Equal((12, 3, 1), (onTime.Passes, late.Passes, pastItsTerm.Passes));
        Assert.Equal(Start.AddMinutes(1), onTime.EndedAt);
        Assert.Equal(Start.AddSeconds(15), late.EndedAt);
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task ARunFailsOnlyWhenItsLastPassFailedAndItsLogCountsThePassesThatFailed()
    {
        var time = new FakeTimeProvider(Start);
        var calls = 0;
        Task<bool> Pass(bool failLast)
        {
            calls++;
            time.Advance(TimeSpan.FromSeconds(20));
            return calls == 1 || (failLast && calls == 3) ? throw new InvalidOperationException($"pass {calls} broke") : Task.FromResult(true);
        }

        var recovered = new JobContext(Guid.NewGuid(), "probe.loop", "a", 1, Start, null, null);
        var healed = await JobLoop.RunAsync(recovered, TimeSpan.FromMinutes(1), TimeSpan.Zero, time, _ => Pass(failLast: false), TestContext.Current.CancellationToken);
        calls = 0;
        time = new FakeTimeProvider(Start);
        var broken = new JobContext(Guid.NewGuid(), "probe.loop", "b", 1, Start, null, null);
        var failed = await JobLoop.RunAsync(broken, TimeSpan.FromMinutes(1), TimeSpan.Zero, time, _ => Pass(failLast: true), TestContext.Current.CancellationToken);

        Assert.True(healed.IsSuccess);
        Assert.Contains("3 pass(es), 1 failed.", recovered.Text, StringComparison.Ordinal);
        Assert.Equal("jobs.pass_failed", failed.Error.Code);
        Assert.Contains("pass 3 broke", failed.Error.Message, StringComparison.Ordinal);
        Assert.Contains("3 pass(es), 2 failed.", broken.Text, StringComparison.Ordinal);
    }

    private static async Task<(int Passes, DateTimeOffset EndedAt)> PassesAsync(DateTimeOffset scheduledFor)
    {
        var time = new FakeTimeProvider(Start);
        var passes = 0;
        var context = new JobContext(Guid.NewGuid(), "probe.loop", "key", 1, scheduledFor, null, null);
        var result = await JobLoop.RunAsync(context, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), time, _ =>
        {
            passes++;
            time.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        return (passes, time.GetUtcNow());
    }
}
