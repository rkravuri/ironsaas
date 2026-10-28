using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Iron.Kernel.Telemetry.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Iron.Kernel.Telemetry.Tests;

/// <summary>
/// The trace exporter while the collector is down (TF-130 AC4, ADR-0056): requests still complete without waiting, the
/// spans wait in a bounded queue, and every span lost is counted. The collector is the image the laptop stack pins, so
/// the exporter talks to what it talks to in a unit. Alone in its collection: every tracer provider of the process sees
/// every span, so another test's requests would land in these counts.
/// </summary>
[Collection(LostSpanCollection.Name)]
public sealed partial class LostSpanTests
{
    private const string CollectorConfig = """
        receivers:
          otlp:
            protocols:
              grpc:
                endpoint: 0.0.0.0:4317
        exporters:
          debug:
            verbosity: basic
        extensions:
          health_check:
            endpoint: 0.0.0.0:13133
        service:
          extensions: [health_check]
          pipelines:
            traces:
              receivers: [otlp]
              exporters: [debug]
            metrics:
              receivers: [otlp]
              exporters: [debug]
            logs:
              receivers: [otlp]
              exporters: [debug]
        """;

    private static readonly TraceExportOptions SmallQueue = new() { QueueSize = 32, BatchSize = 8, DelayMilliseconds = 100, ExportTimeoutMilliseconds = 2000 };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-130 AC4", "ADR-0056")]
    public async Task WhileTheCollectorHangsOrIsStoppedRequestsStillCompleteAndEverySpanLostIsCounted()
    {
        await using var collector = new ContainerBuilder(CollectorImage())
            .WithResourceMapping(Encoding.UTF8.GetBytes(CollectorConfig), "/etc/otelcol/config.yaml")
            .WithCommand("--config=/etc/otelcol/config.yaml")
            .WithPortBinding(4317, true)
            .WithPortBinding(13133, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(13133).ForPath("/")))
            .Build();
        await collector.StartAsync(Cancel);
        using var counts = new SpanCounts();
        await using var host = await ExportingHost.StartAsync($"http://{collector.Hostname}:{collector.GetMappedPublicPort(4317)}");

        // The collector up: every span reaches it, none is lost.
        await host.RequestsAsync(10);
        Assert.True(host.Flush(), "the exporter flushes while the collector is up");
        await SettledAsync(counts, host);
        Assert.True(counts.Exported >= 10, $"the spans reached the collector, {counts.Exported} of at least 10");
        Assert.Equal(0, counts.Dropped);

        // The collector hangs (paused, it takes the connection and never answers): an export waits for its timeout, the
        // queue fills behind it and the spans beyond it are dropped at once, and no request waits for any of it.
        await collector.PauseAsync(Cancel);
        var slowest = await host.RequestsAsync(300);
        Assert.True(slowest < TimeSpan.FromSeconds(1), $"the slowest request took {slowest} while the collector hung");
        await SettledAsync(counts, host);
        Assert.True(counts.DroppedBy(TraceExportMetrics.QueueFull) > 0, "spans beyond the queue are dropped and counted");
        Assert.True(counts.DroppedBy(TraceExportMetrics.ExportFailed) > 0, "spans of an export that timed out are dropped and counted");

        // Back: spans flow again.
        await collector.UnpauseAsync(Cancel);
        var exported = counts.Exported;
        await ExportedAgainAsync(counts, host, exported);

        // Stopped: every export is refused at once; the spans are dropped and counted, none sent.
        await collector.StopAsync(Cancel);
        await SettledAsync(counts, host);
        var before = (host.Ended, counts.Exported, counts.Dropped);
        slowest = await host.RequestsAsync(100);
        Assert.True(slowest < TimeSpan.FromSeconds(1), $"the slowest request took {slowest} with the collector stopped");
        await SettledAsync(counts, host);
        Assert.Equal(before.Exported, counts.Exported);
        Assert.Equal(host.Ended - before.Ended, counts.Dropped - before.Dropped);
    }

    [Fact]
    [Proves("TF-130 AC4", "ADR-0056")]
    public async Task WithNoCollectorAtAllRequestsCompleteAndTheirSpansAreCountedAsLost()
    {
        using var counts = new SpanCounts();
        await using var host = await ExportingHost.StartAsync($"http://127.0.0.1:{UnusedPort()}");

        var slowest = await host.RequestsAsync(100);

        Assert.True(slowest < TimeSpan.FromSeconds(2), $"the slowest request took {slowest}");
        await SettledAsync(counts, host);
        Assert.Equal(0, counts.Exported);
        Assert.True(host.Ended >= 100, $"every request ended a span, {host.Ended} of 100");
    }

    /// <summary>
    /// Until every span ended is sent or counted as lost. A span is sent, dropped or still queued, so once the first two
    /// add up to every span ended the queue is empty.
    /// </summary>
    private static Task SettledAsync(SpanCounts counts, ExportingHost host) => WaitAsync(() => counts.Exported + counts.Dropped == host.Ended);

    /// <summary>Requests until spans reach the collector again: the first export after it wakes may still fail.</summary>
    private static async Task ExportedAgainAsync(SpanCounts counts, ExportingHost host, long exported)
    {
        var watch = Stopwatch.StartNew();
        while (counts.Exported <= exported)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "spans reached the collector again within 30 seconds");
            await host.RequestsAsync(5);
            await Task.Delay(200, Cancel);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        // A refused export fails at once and a hung one within its timeout, so the queue empties within seconds.
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the exporter settled within 30 seconds");
            await Task.Delay(50, Cancel);
        }
    }

    private static int UnusedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CollectorImage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        var compose = File.ReadAllText(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "infra", "compose", "compose.yaml"));
        return CollectorPin().Match(compose) is { Success: true } match ? match.Groups["image"].Value : throw new InvalidOperationException("compose.yaml pins no collector image.");
    }

    [GeneratedRegex(@"image:\s*(?<image>otel/opentelemetry-collector-contrib:[0-9.]+)")]
    private static partial Regex CollectorPin();

    /// <summary>The exporter's counters as a MeterListener reads them, the dropped ones by reason.</summary>
    private sealed class SpanCounts : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _dropped = new(StringComparer.Ordinal);
        private long _exported;

        public SpanCounts()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TraceExportMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "telemetry.spans_exported")
                {
                    Interlocked.Add(ref _exported, value);
                    return;
                }

                var reason = "unknown";
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason")
                    {
                        reason = tag.Value?.ToString() ?? reason;
                    }
                }

                _dropped.AddOrUpdate(reason, value, (_, sum) => sum + value);
            });
            _listener.Start();
        }

        public long Exported => Interlocked.Read(ref _exported);

        public long Dropped => _dropped.Values.Sum();

        public long DroppedBy(string reason) => _dropped.GetValueOrDefault(reason);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A host with the real telemetry pipeline, exporting to the given collector through a small queue.</summary>
    private sealed class ExportingHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly EndedSpans _ended;

        private ExportingHost(WebApplication app, EndedSpans ended)
        {
            _app = app;
            _ended = ended;
            _client = app.GetTestClient();
        }

        /// <summary>Spans the host ended and recorded, each of which the exporter must send or count as lost.</summary>
        public long Ended => _ended.Count;

        public static async Task<ExportingHost> StartAsync(string endpoint)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var ended = new EndedSpans();
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(ended));
            builder.AddIronTelemetry("iron-tests-export", telemetry =>
            {
                telemetry.ExportToCollector = true;
                telemetry.Endpoint = endpoint;
                telemetry.TenantId = "acme";
                telemetry.Traces = SmallQueue;
            });
            var app = builder.Build();
            app.UseIronTelemetry();
            app.MapGet("/ping", () => "pong");
            await app.StartAsync(Cancel);
            return new ExportingHost(app, ended);
        }

        /// <summary>Sends the requests one after another; answers the slowest one's time, each having answered 200.</summary>
        public async Task<TimeSpan> RequestsAsync(int count)
        {
            var slowest = TimeSpan.Zero;
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await _client.GetAsync(new Uri("/ping", UriKind.Relative), Cancel);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
            }

            return slowest;
        }

        public bool Flush() => _app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(Cancel);
            await _app.DisposeAsync();
        }
    }

    /// <summary>Counts the spans the host ends and records, as the exporter sees them.</summary>
    private sealed class EndedSpans : BaseProcessor<Activity>
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public override void OnEnd(Activity data)
        {
            if (data.Recorded)
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LostSpanCollection
{
    public const string Name = "lost spans";
}
