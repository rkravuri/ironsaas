using System.Collections.Concurrent;
using System.Diagnostics;
using Iron.Kernel.Telemetry.Scrubbing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Telemetry.Tests;

/// <summary>The scrubbing rule, the second line that keeps invitation and recovery links out of logs and traces (ADR-0046).</summary>
public class LinkScrubberTests
{
    /// <summary>A code of the real shape: 32 random bytes in base64url.</summary>
    private const string Code = "Xq3vB9kLm2Np7Rt5Wy8Za1Cd4Ef6Gh0Jk-Mn_Pq2Rs4";

    [Fact]
    [Proves("ADR-0046")]
    public void TheRuleReplacesEveryLinksCodeAndLeavesOtherTextAlone()
    {
        var cases = new (string Text, string Expected)[]
        {
            ($"Open https://acme.localhost/recovery#{Code} now", "Open https://acme.localhost/recovery#[redacted] now"),
            ($"POST /auth/recovery/{Code}", "POST /auth/recovery/[redacted]"),
            ($"link /invitations#{Code}", "link /invitations#[redacted]"),
            ($"https://acme.localhost/invitations/{Code}", "https://acme.localhost/invitations/[redacted]"),
            ($"Invitation token: {Code}", "Invitation token: [redacted]"),
            ($"{{\"recovery_token\":\"{Code}\"}}", "{\"recovery_token\":\"[redacted]\"}"),
            ($"/recovery%23{Code}", "/recovery%23[redacted]"),
        };
        var untouched = new[] { "Job 01a15f3c-3a01-7c7f-962c-284effa4aef4 completed", "/recovery#short", "The recovery link was handed over to the operator who asked." };

        Assert.All(cases, c => Assert.Equal(c.Expected, LinkScrubber.Scrub(c.Text)));
        Assert.All(untouched, text => Assert.Equal(text, LinkScrubber.Scrub(text)));
    }

    [Fact]
    [Proves("ADR-0046")]
    public async Task AHostsLogLinesComeOutScrubbedInTheirMessageAndTheirValues()
    {
        var lines = new CapturingProvider();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(lines);
        builder.AddIronTelemetry("scrub-test", options => options.ExportToCollector = false);
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Iron.Test");

        logger.LogInformation("The link {Link} was opened", $"https://acme.localhost/recovery#{Code}");
        logger.LogWarning($"Recovery token: {Code}");

        Assert.Equal(["The link https://acme.localhost/recovery#[redacted] was opened", "Recovery token: [redacted]"], lines.Messages);
        Assert.DoesNotContain(lines.Values, value => value.Contains(Code, StringComparison.Ordinal));
        Assert.Contains(lines.Values, value => value == "https://acme.localhost/recovery#[redacted]");
    }

    [Fact]
    [Proves("ADR-0046")]
    public void ASpansNameAndTagsAreScrubbed()
    {
        using var activity = new Activity($"POST /auth/recovery/{Code}");
        activity.SetTag("url.path", $"/auth/recovery/{Code}");
        activity.SetTag("url.full", $"https://acme.localhost/invitations/{Code}");
        activity.SetTag("http.route", "/auth/recovery");

        ScrubbingActivityProcessor.Scrub(activity);

        Assert.Equal("POST /auth/recovery/[redacted]", activity.DisplayName);
        Assert.Equal("/auth/recovery/[redacted]", activity.GetTagItem("url.path"));
        Assert.Equal("https://acme.localhost/invitations/[redacted]", activity.GetTagItem("url.full"));
        Assert.Equal("/auth/recovery", activity.GetTagItem("http.route"));
    }

    /// <summary>A provider as the console is: registered before the telemetry, so the telemetry wraps it.</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly ConcurrentQueue<string> _values = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IReadOnlyList<string> Values => [.. _values];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner._messages.Enqueue(formatter(state, exception));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (var pair in values.Where(pair => pair.Key != "{OriginalFormat}"))
                    {
                        owner._values.Enqueue(pair.Value?.ToString() ?? string.Empty);
                    }
                }
            }
        }
    }
}
