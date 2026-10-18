using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Iron.Kernel;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace Iron.Telemetry.Tests;

[Collection(TelemetryCollection.Name)]
public class TelemetryTests(TelemetryFixture fixture)
{
    [Fact]
    public async Task ARequestProducesASpanAndReturnsItsTraceIdAsCorrelationId()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName));
        Assert.True(CorrelationId.TryParse(header, out _));

        // The span is exported when the request activity stops, which can be a moment after the response arrives.
        Activity? span = null;
        for (var attempt = 0; attempt < 40 && span is null; attempt++)
        {
            span = fixture.ExportedSpans.FirstOrDefault(s => s.TraceId.ToHexString() == header);
            if (span is null)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotNull(span);
        Assert.Equal("GET /ping", span.DisplayName);
    }

    [Fact]
    public void TheResourceNamesTheServiceAndTheTenant()
    {
        var attributes = fixture.TracerProvider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("iron-tests", attributes["service.name"]);
        Assert.Equal("foundation", attributes["service.namespace"]);
        Assert.Equal("acme", attributes["tenant.id"]);
        Assert.True(attributes.ContainsKey("deployment.environment.name"));
    }

    [Fact]
    public async Task LiveAnswersWithoutRunningChecks()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task ReadyRunsTheTaggedChecksAndNamesTheFailingOne()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unhealthy", json.GetProperty("status").GetString());
        var checks = json.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(2, checks.Count);
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "not-ready" && c.GetProperty("status").GetString() == "unhealthy");
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "always-up" && c.GetProperty("status").GetString() == "healthy");
        Assert.DoesNotContain(checks, c => c.GetProperty("name").GetString() == "untagged");
    }
}
