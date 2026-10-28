using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Protection.Services;

namespace Iron.Kernel.Protection.Tests;

public class RateLimitTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-072 AC1", "ADR-0053")]
    public async Task TheEleventhSignInAttemptInAMinuteFromOneAddressIsRefusedWithRetryAfter()
    {
        await using var host = await ProtectionHost.StartAsync();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var allowed = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/auth/login"), Cancel);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var eleventh = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/auth/login"), Cancel);
        using var otherAddress = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/auth/login", "198.51.100.9"), Cancel);

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        var wait = int.Parse(eleventh.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(wait, 1, 60);
        using var problem = JsonDocument.Parse(await eleventh.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(ProtectionErrors.RateLimited, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(wait, problem.RootElement.GetProperty("retry_after_seconds").GetInt32());
        Assert.Equal(HttpStatusCode.OK, otherAddress.StatusCode);
    }

    [Fact]
    [Proves("TF-072 AC2", "ADR-0053")]
    public async Task APrincipalAtItsApiLimitIsRefusedAndTheRefusalIsCountedAndAuditedOnceAWindow()
    {
        await using var host = await ProtectionHost.StartAsync();
        host.Settings.Set(ProtectionSettings.ApiPerMinute, 60);
        var principal = Guid.NewGuid();
        var counted = new List<string>();
        using var listener = Listen(counted);

        for (var call = 1; call <= 60; call++)
        {
            using var allowed = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", principal: principal), Cancel);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", principal: principal), Cancel);
        using var again = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", principal: principal), Cancel);
        using var someoneElse = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", principal: Guid.NewGuid()), Cancel);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, someoneElse.StatusCode);
        Assert.Equal(["api", "api"], counted);
        var audited = Assert.Single(host.Audit.Events);
        Assert.Equal((ProtectionAuditEvents.RateLimited, AuditOutcome.Denied, principal, AuditSeverity.Low), (audited.Action, audited.Outcome, audited.PrincipalId, audited.Severity));
        Assert.Equal(("api", "principal", "60"), (audited.Details!["limit"], audited.Details["subject"], audited.Details["permits"]));
    }

    [Fact]
    [Proves("TF-072 AC3", "ADR-0053")]
    public async Task ATenantsChangedLimitAppliesAtTheNextRequestWithinTheOperatorsBounds()
    {
        await using var host = await ProtectionHost.StartAsync();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var before = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/auth/login"), Cancel);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // The setting as a save leaves it in the cache; the limiter reads it at every request.
        host.Settings.Set(ProtectionSettings.LoginPerMinute, 3);
        var answers = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var after = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/auth/login"), Cancel);
            answers.Add(after.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], answers);
        Assert.True(RateLimitBounds.Allows("login", JsonSerializer.SerializeToElement(60)));
        Assert.False(RateLimitBounds.Allows("login", JsonSerializer.SerializeToElement(61)), "Above the operator's maximum is refused when saved.");
        Assert.False(RateLimitBounds.Allows("login", JsonSerializer.SerializeToElement(2)), "Below the operator's minimum is refused when saved.");
        Assert.Equal(60, RateLimitBounds.Clamp("login", 500));
    }

    [Fact]
    [Proves("ADR-0053")]
    public async Task ExportsArePerPrincipalAndRecoveryLinksPerAccountEachWithItsOwnWindow()
    {
        await using var host = await ProtectionHost.StartAsync();
        host.Settings.Set(ProtectionSettings.ExportsPerHour, 2);
        host.Settings.Set(ProtectionSettings.RecoveryPerHour, 1);
        var principal = Guid.NewGuid();

        var exports = new List<HttpStatusCode>();
        for (var export = 1; export <= 3; export++)
        {
            using var answer = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/api/v1/things/export", principal: principal), Cancel);
            exports.Add(answer.StatusCode);
        }

        using var firstLink = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/api/v1/people/ada/recovery-link", principal: principal), Cancel);
        using var secondLink = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/api/v1/people/ada/recovery-link", principal: principal), Cancel);
        using var otherAccount = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Post, "/api/v1/people/grace/recovery-link", principal: principal), Cancel);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], exports);
        var hour = int.Parse(secondLink.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.OK), (firstLink.StatusCode, secondLink.StatusCode, otherAccount.StatusCode));
        Assert.InRange(hour, 60, 3600);
    }

    private static MeterListener Listen(List<string> limits)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ProtectionMetrics.MeterName && instrument.Name == "protection.rate_limited")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "limit")
                {
                    lock (limits)
                    {
                        limits.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();
        return listener;
    }
}
