using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Iron.Kernel.Telemetry.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Telemetry.Clock;

/// <summary>
/// Where each process checks its clock (<c>Telemetry:Clock</c>, TF-065 AC3, ADR-0050): a time server reached by NTP,
/// how often, and how far off is too far. One second, as TF-065 says.
/// </summary>
public sealed class ClockOptions
{
    public const string SectionName = "Telemetry:Clock";

    public bool Enabled { get; set; } = true;

    public string Server { get; set; } = "pool.ntp.org";

    public int Port { get; set; } = 123;

    public int IntervalMinutes { get; set; } = 10;

    public double AlertSeconds { get; set; } = 1.0;

    public int TimeoutSeconds { get; set; } = 3;
}

/// <summary>A process's clock as last measured: how far it is from the time server, and when.</summary>
public sealed class ClockState
{
    public double? OffsetSeconds { get; set; }

    public DateTimeOffset? MeasuredAt { get; set; }

    public string? Problem { get; set; }
}

/// <summary>
/// A simple NTP client (SNTP, RFC 4330): one request, and the clock's offset from the server's two times and the local
/// two, as <c>((t2 - t1) + (t3 - t4)) / 2</c>. The local times come from <see cref="TimeProvider"/>, the process's clock.
/// </summary>
public static class Sntp
{
    private static readonly DateTimeOffset Era = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<double> OffsetSecondsAsync(string server, int port, TimeProvider time, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        using var client = new UdpClient();
        client.Connect(server, port);
        var request = new byte[48];
        request[0] = 0x23; // no leap warning, version 4, client
        var sent = time.GetUtcNow();
        Write(request.AsSpan(40), sent);
        await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var answer = await client.ReceiveAsync(limit.Token).ConfigureAwait(false);
        var received = time.GetUtcNow();
        if (answer.Buffer.Length < 48)
        {
            throw new InvalidOperationException($"The time server {server} answered {answer.Buffer.Length} bytes, not an NTP packet.");
        }

        var serverReceived = Read(answer.Buffer.AsSpan(32));
        var serverSent = Read(answer.Buffer.AsSpan(40));
        return ((serverReceived - sent).TotalSeconds + (serverSent - received).TotalSeconds) / 2;
    }

    /// <summary>An NTP timestamp: seconds since 1900 and a binary fraction, both big-endian.</summary>
    public static void Write(Span<byte> target, DateTimeOffset at)
    {
        var since = at - Era;
        var seconds = (uint)Math.Floor(since.TotalSeconds);
        var fraction = (uint)((since.TotalSeconds - seconds) * 4294967296.0);
        BinaryPrimitives.WriteUInt32BigEndian(target, seconds);
        BinaryPrimitives.WriteUInt32BigEndian(target[4..], fraction);
    }

    public static DateTimeOffset Read(ReadOnlySpan<byte> source)
    {
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(source);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return Era.AddSeconds(seconds + (fraction / 4294967296.0));
    }
}

/// <summary>The clock's measures, exported with every <c>Iron.*</c> meter.</summary>
public static class ClockMetrics
{
    public static readonly Meter Meter = new("Iron.Clock");
}

/// <summary>
/// Measures this process's clock against the time server every few minutes (TF-065 AC3, ADR-0050). Every stored time is
/// the server's, so a server whose clock drifts writes wrong times for everyone: an offset above the limit raises the
/// alert (an error line starting <c>ALERT:</c>, the gauge <c>clock.offset_seconds</c>, the readiness check <c>clock</c>
/// Degraded). A time server it cannot reach is a warning, not an alert: the clock may be right.
/// </summary>
public sealed class ClockCheck(ClockOptions options, ClockState state, TimeProvider time, ILogger<ClockCheck> logger)
{
    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _alertedAt = DateTimeOffset.MinValue;

    public async Task<double?> CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var offset = await Sntp.OffsetSecondsAsync(options.Server, options.Port, time, TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)), cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            state.OffsetSeconds = offset;
            state.MeasuredAt = now;
            state.Problem = Math.Abs(offset) > options.AlertSeconds ? $"The clock is {offset:+0.000;-0.000} seconds off {options.Server}." : null;
            if (state.Problem is not null && now - _alertedAt >= AlertEvery)
            {
                _alertedAt = now;
                logger.LogError("ALERT: this process's clock is {Offset} seconds off the time server {Server}; every time it stores is off by as much. Synchronise the host's clock.", offset, options.Server);
            }

            return offset;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The time server {Server} could not be asked for the time: {Message}", options.Server, exception.Message);
            return null;
        }
    }
}

public sealed class ClockCheckService(ClockCheck check, ClockOptions options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)), time);
        do
        {
            await check.CheckOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Degraded while the last measure was too far off: the process works, and every time it stores is suspect.</summary>
public sealed class ClockHealthCheck(ClockState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Problem is { } problem
            ? HealthCheckResult.Degraded(problem)
            : HealthCheckResult.Healthy(state.OffsetSeconds is { } offset ? $"The clock is {offset:+0.000;-0.000} seconds off the time server." : "The clock has not been measured yet."));
}

public static class ClockCheckExtensions
{
    /// <summary>The clock's check in a host (TF-065 AC3): every process measures its own clock.</summary>
    public static IServiceCollection AddIronClockCheck(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(ClockOptions.SectionName).Get<ClockOptions>() ?? new ClockOptions();
        services.AddSingleton(options);
        var state = new ClockState();
        services.AddSingleton(state);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ClockCheck>();
        ClockMetrics.Meter.CreateObservableGauge("clock.offset_seconds", () => state.OffsetSeconds ?? 0, "s", "How far this process's clock is from the time server");
        services.AddHealthChecks().AddCheck<ClockHealthCheck>("clock", HealthStatus.Degraded, [HealthEndpoints.ReadyTag]);
        if (options.Enabled)
        {
            services.AddHostedService<ClockCheckService>();
        }

        return services;
    }
}
