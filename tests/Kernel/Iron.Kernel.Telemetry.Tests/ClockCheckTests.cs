using System.Net;
using System.Net.Sockets;
using Iron.Kernel.Telemetry.Clock;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Telemetry.Tests;

/// <summary>
/// The clock's check (TF-065 AC3, ADR-0050): against a time server on this machine that answers a time of the test's
/// choosing, a process clock more than a second off raises the alert and makes the readiness check degraded; one within
/// it is healthy; an unreachable server is a warning, not an alert.
/// </summary>
public class ClockCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    [Proves("TF-065 AC3", "ADR-0050")]
    public async Task AClockMoreThanASecondOffRaisesTheAlertAndOneWithinItIsHealthy()
    {
        var time = new FakeTimeProvider(Now);
        var logs = new Lines();
        using var server = new FakeTimeServer();
        var state = new ClockState();
        var check = new ClockCheck(new ClockOptions { Server = "127.0.0.1", Port = server.Port, TimeoutSeconds = 2 }, state, time, logs.For<ClockCheck>());

        server.Answer = Now.AddSeconds(3.2);
        var offset = await check.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.InRange(offset!.Value, 3.1, 3.3);
        Assert.Contains(logs.All, line => line.StartsWith("Error ALERT: this process's clock is", StringComparison.Ordinal));
        Assert.Equal(HealthStatus.Degraded, (await new ClockHealthCheck(state).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        server.Answer = Now.AddSeconds(0.3);
        Assert.InRange((await check.CheckOnceAsync(TestContext.Current.CancellationToken))!.Value, 0.2, 0.4);
        Assert.Equal(HealthStatus.Healthy, (await new ClockHealthCheck(state).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task ATimeServerThatDoesNotAnswerIsAWarningNotAnAlert()
    {
        var logs = new Lines();
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var check = new ClockCheck(new ClockOptions { Server = "127.0.0.1", Port = port, TimeoutSeconds = 1 }, new ClockState(), new FakeTimeProvider(Now), logs.For<ClockCheck>());

        Assert.Null(await check.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains(logs.All, line => line.StartsWith("Warning The time server", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.All, line => line.Contains("ALERT", StringComparison.Ordinal));
    }

    /// <summary>An NTP server on the loopback that answers with the time a test sets, as its receive and transmit times.</summary>
    private sealed class FakeTimeServer : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();

        public FakeTimeServer()
        {
            _ = Task.Run(ServeAsync);
        }

        public DateTimeOffset Answer { get; set; }

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var request = await _socket.ReceiveAsync(_stop.Token);
                    var reply = new byte[48];
                    reply[0] = 0x24; // version 4, server
                    request.Buffer.AsSpan(40, 8).CopyTo(reply.AsSpan(24));
                    Sntp.Write(reply.AsSpan(32), Answer);
                    Sntp.Write(reply.AsSpan(40), Answer);
                    await _socket.SendAsync(reply, request.RemoteEndPoint, _stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }

    private sealed class Lines : ILoggerProvider
    {
        public List<string> All { get; } = [];

        public ILogger<T> For<T>() => new LoggerFactory([this]).CreateLogger<T>();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Lines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.All)
                {
                    owner.All.Add($"{logLevel} {formatter(state, exception)}");
                }
            }
        }
    }
}
