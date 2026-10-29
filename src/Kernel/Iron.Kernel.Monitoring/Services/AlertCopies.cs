using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>
/// The control plane's store of the units' alerts (ADR-0056): a copy is kept by the tenant and the unit's id, and a later
/// revision replaces it, so the operator's alerting follows the unit's acknowledge and resolve.
/// </summary>
public sealed class AlertCopyStore(MonitoringDbContext context) : IAlertCopies
{
    public async Task KeepAsync(string tenant, AlertCopy copy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(copy);
        var kept = await context.Alerts.SingleOrDefaultAsync(a => a.Tenant == tenant && a.SourceId == copy.Id, cancellationToken).ConfigureAwait(false);
        if (kept is null)
        {
            context.Alerts.Add(Alert.CopyOf(tenant, copy));
        }
        else if (kept.Origin == Alert.Copy && copy.Revision > kept.Revision)
        {
            kept.Take(copy);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Sends the control plane every alert of the unit that changed since it last did (ADR-0056), as the unit's own client,
/// with its revision. A person is named by their id, a login identifier masked: the person's details stay in the tenant.
/// A unit without a control plane keeps its alerts to itself, and says so once.
/// </summary>
public sealed class AlertCopySender(MonitoringDbContext context, ControlPlaneClient client, ILogger<AlertCopySender> logger)
{
    public const int BatchSize = 100;

    private static int _saidUnconfigured;

    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        if (!client.IsConfigured)
        {
            if (Interlocked.Exchange(ref _saidUnconfigured, 1) == 0)
            {
                logger.LogWarning("No control plane is configured (ControlPlane:*): this unit's security alerts stay in its own database and reach no operator.");
            }

            return 0;
        }

        var waiting = await context.Alerts
            .Where(a => a.Origin == Alert.Local && a.CopiedRevision < a.Revision)
            .OrderBy(a => a.RaisedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sent = 0;
        foreach (var alert in waiting)
        {
            var copy = alert.ToCopy(Masked(alert.GroupKind, alert.GroupValue));
            try
            {
                using var answer = await client.SendAsync(HttpMethod.Put, $"/api/v1/alert-copies/{alert.Id:D}", copy, cancellationToken).ConfigureAwait(false);
                if (!answer.IsSuccessStatusCode)
                {
                    MonitoringMetrics.CopyFailures.Add(1);
                    logger.LogWarning("The control plane refused the copy of alert {AlertId}: {Status}. It is sent again at the next pass.", alert.Id, (int)answer.StatusCode);
                    continue;
                }
            }
            catch (HttpRequestException exception)
            {
                MonitoringMetrics.CopyFailures.Add(1);
                logger.LogWarning(exception, "The control plane could not be reached with alert {AlertId}; it is sent again at the next pass.", alert.Id);
                return sent;
            }

            alert.Copied(copy.Revision);
            sent++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }

    /// <summary>A login identifier shows its first letter and its domain; every other group is an id or an address.</summary>
    public static string Masked(string groupKind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (groupKind != "target" || value.Length == 0)
        {
            return value;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? $"{value[0]}…{value[at..]}" : $"{value[0]}…";
    }
}

/// <summary>
/// Tells the operator's alerting about every alert the control plane holds (ADR-0056), through Alertmanager's API:
/// an open alert is sent at every pass, so it stays firing; an acknowledged or resolved one is sent once with its end,
/// so the page stops. The labels are what routing needs (the rule, the tenant, the severity), the annotations what the
/// person paged reads (the title, the counts, the link to the events and the runbook).
/// </summary>
public sealed class AlertmanagerForwarder(MonitoringDbContext context, HttpClient http, AlertmanagerOptions options, ILogger<AlertmanagerForwarder> logger)
{
    public const int BatchSize = 500;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Address);

    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return 0;
        }

        var alerts = await context.Alerts
            .Where(a => a.State == AlertStates.Open || a.SentRevision < a.Revision)
            .OrderByDescending(a => a.RaisedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (alerts.Count == 0)
        {
            return 0;
        }

        var body = alerts.Select(Posted).ToList();
        try
        {
            using var answer = await http.PostAsJsonAsync(new Uri(new Uri(options.Address!), "/api/v2/alerts"), body, Json, cancellationToken).ConfigureAwait(false);
            if (!answer.IsSuccessStatusCode)
            {
                MonitoringMetrics.AlertmanagerFailures.Add(1);
                logger.LogError("ALERT: the operator's alerting refused {Count} alert(s): {Status}. They are sent again at the next pass.", alerts.Count, (int)answer.StatusCode);
                return 0;
            }
        }
        catch (HttpRequestException exception)
        {
            MonitoringMetrics.AlertmanagerFailures.Add(1);
            logger.LogError(exception, "ALERT: the operator's alerting at {Address} could not be reached with {Count} alert(s); they are sent again at the next pass.", options.Address, alerts.Count);
            return 0;
        }

        foreach (var alert in alerts)
        {
            alert.Sent(alert.Revision);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return alerts.Count;
    }

    /// <summary>An alert as Alertmanager's API takes it.</summary>
    public static PostedAlert Posted(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alertname"] = alert.Rule,
            ["tenant"] = alert.Tenant,
            ["severity"] = alert.Severity,
            ["kind"] = alert.Kind,
            ["alert_id"] = alert.SourceId.ToString("D"),
        };
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["summary"] = alert.Title,
            ["description"] = alert.Kind == AlertKinds.Failing
                ? alert.Reason ?? "The rule is failing."
                : $"{alert.Count.ToString(CultureInfo.InvariantCulture)} for {alert.GroupKind} {alert.GroupValue} within {alert.WindowMinutes.ToString(CultureInfo.InvariantCulture)} minutes (threshold {alert.Threshold.ToString(CultureInfo.InvariantCulture)}).",
            ["state"] = alert.State,
        };
        if (alert.Link is not null)
        {
            annotations["link"] = alert.Link;
        }

        if (alert.Runbook is not null)
        {
            annotations["runbook"] = alert.Runbook;
        }

        var ended = alert.State == AlertStates.Resolved ? alert.ResolvedAt : alert.State == AlertStates.Acknowledged ? alert.AcknowledgedAt : null;
        return new PostedAlert(labels, annotations, alert.RaisedAt, ended);
    }
}

/// <summary>One alert of Alertmanager's v2 API: no end while it fires.</summary>
public sealed record PostedAlert(
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels,
    [property: JsonPropertyName("annotations")] IReadOnlyDictionary<string, string> Annotations,
    [property: JsonPropertyName("startsAt")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("endsAt")] DateTimeOffset? EndsAt);
