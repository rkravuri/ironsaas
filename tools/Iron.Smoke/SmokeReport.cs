using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Smoke;

/// <summary>A step's outcome, with the requirement criteria and decisions it proves (ADR-0044), which make trace reads.</summary>
public sealed record StepResult(string Name, IReadOnlyList<string> Proves, string Status, long DurationMs, string Detail);

/// <summary>
/// Runs the steps in order, prints each as it ends with what it proves, stops at the first failure, and writes the JSON
/// report the pipeline uploads and make trace reads.
/// </summary>
public sealed class SmokeReport(string tenant, string reportPath)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly List<StepResult> _steps = [];
    private readonly DateTimeOffset _startedAt = TimeProvider.System.GetUtcNow();

    public bool Passed { get; private set; } = true;

    public async Task<bool> StepAsync(string name, string[] proves, Func<Task<string>> run)
    {
        ArgumentNullException.ThrowIfNull(proves);
        var list = $"[{string.Join(", ", proves)}]";
        if (!Passed)
        {
            _steps.Add(new StepResult(name, proves, "skipped", 0, "an earlier step failed"));
            Console.WriteLine($"  skipped   {name,-16} {list}");
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var detail = await run().ConfigureAwait(false);
            _steps.Add(new StepResult(name, proves, "passed", stopwatch.ElapsedMilliseconds, detail));
            Console.WriteLine($"  passed    {name,-16} {list} ({stopwatch.ElapsedMilliseconds} ms)  {detail}");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Passed = false;
            var detail = exception is SmokeException ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";
            _steps.Add(new StepResult(name, proves, "failed", stopwatch.ElapsedMilliseconds, detail));
            Console.WriteLine($"  FAILED    {name,-16} {list} ({stopwatch.ElapsedMilliseconds} ms)  {detail}");
            return false;
        }
    }

    public async Task WriteAsync()
    {
        var report = new
        {
            tenant,
            started_at = _startedAt,
            finished_at = TimeProvider.System.GetUtcNow(),
            passed = Passed,
            steps = _steps,
        };
        // make smoke writes into artifacts/test-results/smoke, which a fresh checkout does not have yet.
        var folder = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(Passed ? $"Smoke passed: {_steps.Count(s => s.Status == "passed")} steps. Report: {reportPath}" : $"Smoke FAILED at {_steps.First(s => s.Status == "failed").Name}. Report: {reportPath}");
    }
}

public sealed class SmokeException(string message) : Exception(message);
