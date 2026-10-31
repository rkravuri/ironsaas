using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// The jobs every process runs for itself (ADR-0061): what a process keeps in its own memory, such as the cached flags,
/// settings and entitlements, which no other process can refresh for it. Each runs on its interval with its timeout; a
/// failure is logged and tried again by its retry policy, and after the last attempt the next interval tries it again.
/// Nothing is recorded in the database: the job console lists these jobs as run in each process.
/// </summary>
public sealed class ProcessJobRunner(IServiceScopeFactory scopes, JobDeclarations declarations, TimeProvider time, ILogger<ProcessJobRunner> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(declarations.ProcessJobs.Select(job => LoopAsync(job, stoppingToken)));

    private async Task LoopAsync(DeclaredJob job, CancellationToken stoppingToken)
    {
        var every = job.Every!.Value;
        var due = time.GetUtcNow() + job.Definition.Schedule.FirstAfter;
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = due - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
                }

                var error = await RunAsync(job, failures + 1, stoppingToken).ConfigureAwait(false);
                var now = time.GetUtcNow();
                if (error is null)
                {
                    failures = 0;
                    due = Next(due, every, now);
                    continue;
                }

                failures++;
                if (failures < job.Definition.Retry.Attempts)
                {
                    logger.LogWarning("The job {Job} failed in this process ({Error}); it is tried again, attempt {Attempt} of {Attempts}.", job.Name, error, failures + 1, job.Definition.Retry.Attempts);
                    due = now + job.Definition.Retry.DelayAfter(failures, RetryPolicy.Jitter());
                }
                else
                {
                    logger.LogError("The job {Job} failed {Attempts} times in this process ({Error}); the next interval runs it again.", job.Name, failures, error);
                    failures = 0;
                    due = Next(due, every, now);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One run in a scope of its own, cancelled at its timeout; answers the error, or null when it succeeded.</summary>
    private async Task<string?> RunAsync(DeclaredJob job, int attempt, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(job.Definition.Timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = (IJob)scope.ServiceProvider.GetRequiredService(job.Definition.HandlerType);
            var context = new JobContext(Guid.Empty, job.Name, "process", attempt, time.GetUtcNow(), null, null);
            var result = await handler.RunAsync(context, timeout.Token).ConfigureAwait(false);
            return result.IsSuccess ? null : $"{result.Error.Code}: {result.Error.Message}";
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return $"timed out after {Stopwatch.GetElapsedTime(started).TotalSeconds:0.#} s";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The job {Job} threw in this process.", job.Name);
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static DateTimeOffset Next(DateTimeOffset due, TimeSpan every, DateTimeOffset now)
    {
        var next = due + every;
        return next > now ? next : now + every;
    }
}
