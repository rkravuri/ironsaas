using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;

namespace Iron.Kernel.Telemetry.Export;

/// <summary>
/// The size of the trace exporter's queue and its batches (TF-130 AC4, ADR-0056), bound from <c>Telemetry:Traces</c>.
/// The defaults are the OpenTelemetry SDK's, so a host behaves as it did with the SDK's own batch processor.
/// </summary>
public sealed class TraceExportOptions
{
    /// <summary>How many finished spans wait for the exporter; a span beyond them is dropped and counted.</summary>
    public int QueueSize { get; set; } = 2048;

    /// <summary>How many spans one export sends at most.</summary>
    public int BatchSize { get; set; } = 512;

    /// <summary>How long the exporter waits for a batch to fill before it sends what it has.</summary>
    public int DelayMilliseconds { get; set; } = 5000;

    /// <summary>How long one export may wait for a collector that does not answer, the OTLP exporter's own default.</summary>
    public int ExportTimeoutMilliseconds { get; set; } = 10_000;
}

/// <summary>
/// The counters of the trace exporter (TF-130 AC4): every span sent, and every span lost with why, so the loss shows in
/// Prometheus once the collector is back, the counters being cumulative.
/// </summary>
public static class TraceExportMetrics
{
    public const string MeterName = "Iron.Telemetry";

    /// <summary>The <c>reason</c> of a dropped span: the queue was full, or the export it was in failed.</summary>
    public const string QueueFull = "queue_full";

    public const string ExportFailed = "export_failed";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> SpansDropped = Meter.CreateCounter<long>("telemetry.spans_dropped", unit: "{span}", description: "Spans the trace exporter lost, by reason: its queue was full, or the export they were in failed.");

    public static readonly Counter<long> SpansExported = Meter.CreateCounter<long>("telemetry.spans_exported", unit: "{span}", description: "Spans the trace exporter sent to the collector.");

    private static readonly ConcurrentDictionary<CountedBatchExportProcessor, byte> Queues = new();

    /// <summary>The spans waiting for the exporter: the buffer that fills while the collector is down.</summary>
    public static readonly ObservableGauge<long> SpansQueued = Meter.CreateObservableGauge("telemetry.spans_queued", () => Queues.Keys.Sum(queue => (long)queue.Queued), unit: "{span}", description: "Spans waiting in the trace exporter's queue.");

    internal static void Watch(CountedBatchExportProcessor queue) => Queues.TryAdd(queue, 0);

    internal static void Forget(CountedBatchExportProcessor queue) => Queues.TryRemove(queue, out _);
}

/// <summary>
/// The trace exporter's queue (TF-130 AC4, ADR-0056). A request that ends a span only puts it on a bounded queue and
/// never waits; a thread of the processor's own sends the queue in batches. When the collector is down, the export of a
/// batch fails and its spans are dropped, and while it fails the queue fills and further spans are dropped at once:
/// every one is counted in <c>telemetry.spans_dropped</c>. The SDK's own batch processor behaves the same way but keeps
/// its count to itself, which is why this one exists.
/// </summary>
public sealed class CountedBatchExportProcessor : BaseExportProcessor<Activity>
{
    private readonly ConcurrentQueue<Activity> _queue = new();
    private readonly TraceExportOptions _options;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    // Monitor's wait and pulse, which System.Threading.Lock does not offer, hand a flush back to its caller.
    private readonly object _flushes = new();
    private readonly Thread _thread;
    private int _queued;
    private bool _disposed;
    private long _flushesAsked;
    private long _flushesDone;

    public CountedBatchExportProcessor(BaseExporter<Activity> exporter, TraceExportOptions options)
        : base(exporter)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = new TraceExportOptions
        {
            QueueSize = Math.Max(1, options.QueueSize),
            BatchSize = Math.Clamp(options.BatchSize, 1, Math.Max(1, options.QueueSize)),
            DelayMilliseconds = Math.Max(1, options.DelayMilliseconds),
            ExportTimeoutMilliseconds = Math.Max(1, options.ExportTimeoutMilliseconds),
        };

        // A thread of its own, not the thread pool's: an export that waits on a dead collector must not starve requests.
        _thread = new Thread(Run) { IsBackground = true, Name = "Iron trace exporter" };
        _thread.Start();
        TraceExportMetrics.Watch(this);
    }

    /// <summary>The spans waiting for the exporter now.</summary>
    public int Queued => Volatile.Read(ref _queued);

    protected override void OnExport(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.Recorded)
        {
            return;
        }

        if (Interlocked.Increment(ref _queued) > _options.QueueSize)
        {
            Interlocked.Decrement(ref _queued);
            TraceExportMetrics.SpansDropped.Add(1, new KeyValuePair<string, object?>("reason", TraceExportMetrics.QueueFull));
            return;
        }

        _queue.Enqueue(data);
        if (Volatile.Read(ref _queued) >= _options.BatchSize)
        {
            _wake.Set();
        }
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        var asked = Interlocked.Increment(ref _flushesAsked);
        _wake.Set();
        lock (_flushes)
        {
            while (Interlocked.Read(ref _flushesDone) < asked)
            {
                var left = timeoutMilliseconds == Timeout.Infinite ? Timeout.Infinite : timeoutMilliseconds - (int)watch.ElapsedMilliseconds;
                if (left != Timeout.Infinite && left <= 0)
                {
                    return false;
                }

                Monitor.Wait(_flushes, left);
            }
        }

        return base.OnForceFlush(Remaining(timeoutMilliseconds, watch));
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        _stop.Set();
        var joined = timeoutMilliseconds == Timeout.Infinite ? _thread.Join(Timeout.Infinite) : _thread.Join(Math.Max(0, timeoutMilliseconds));
        return base.OnShutdown(Remaining(timeoutMilliseconds, watch)) && joined;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            TraceExportMetrics.Forget(this);
            _stop.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _wake.Dispose();
            _stop.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int Remaining(int timeoutMilliseconds, Stopwatch watch) =>
        timeoutMilliseconds == Timeout.Infinite ? Timeout.Infinite : Math.Max(0, timeoutMilliseconds - (int)watch.ElapsedMilliseconds);

    private void Run()
    {
        var batch = new Activity[_options.BatchSize];
        WaitHandle[] handles = [_wake, _stop];
        while (true)
        {
            var stopping = WaitHandle.WaitAny(handles, _options.DelayMilliseconds) == 1;
            var asked = Interlocked.Read(ref _flushesAsked);
            ExportQueued(batch);
            lock (_flushes)
            {
                Interlocked.Exchange(ref _flushesDone, asked);
                Monitor.PulseAll(_flushes);
            }

            if (stopping)
            {
                return;
            }
        }
    }

    private void ExportQueued(Activity[] batch)
    {
        while (true)
        {
            var count = 0;
            while (count < batch.Length && _queue.TryDequeue(out var activity))
            {
                batch[count++] = activity;
            }

            if (count == 0)
            {
                return;
            }

            Interlocked.Add(ref _queued, -count);
            ExportResult result;
            try
            {
                result = exporter.Export(new Batch<Activity>(batch, count));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The exporter reports its own failures; one that throws loses its batch the same way.
                result = ExportResult.Failure;
            }

            if (result == ExportResult.Success)
            {
                TraceExportMetrics.SpansExported.Add(count);
            }
            else
            {
                TraceExportMetrics.SpansDropped.Add(count, new KeyValuePair<string, object?>("reason", TraceExportMetrics.ExportFailed));
            }

            Array.Clear(batch, 0, count);
            if (count < batch.Length)
            {
                return;
            }
        }
    }
}
