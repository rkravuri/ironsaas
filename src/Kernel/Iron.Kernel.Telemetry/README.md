# Iron.Kernel.Telemetry

Observable by default (P6, TF-130, ADR-0010). No schema.

| Namespace | What is in it |
|---|---|
| `Iron.Kernel.Telemetry` | `AddIronTelemetry(serviceName)` on the host builder: OpenTelemetry traces, metrics and logs with the service name, the environment and the tenant on every signal; `UseIronTelemetry()` for the correlation middleware; `TelemetryOptions` |
| `Iron.Kernel.Telemetry.Correlation` | `CorrelationMiddleware` (fixes the trace id as the request's correlation id and returns it in `X-Correlation-Id`) and `RequestCorrelation.Get(HttpContext)` |
| `Iron.Kernel.Telemetry.Health` | `AddIronHealth()` and `MapIronHealth()`: `/health/live` and `/health/ready`, with checks tagged `ready`; `HealthProbe`, the `health` command container runtimes use as HEALTHCHECK |
| `Iron.Kernel.Telemetry.Scrubbing` | The scrubbing rule for invitation and recovery links (ADR-0046), the second line behind their never being written: `LinkScrubber`, `ScrubbingLoggerProvider`, which `AddIronTelemetry` wraps around every log provider (message and values), and `ScrubbingActivityProcessor` with the request's enrichment (a span's name and tags) |
| `Iron.Kernel.Telemetry.Clock` | `AddIronClockCheck()`: each process measures its clock against a time server by SNTP (`Telemetry:Clock`: `Server` `pool.ntp.org`, `Port`, `IntervalMinutes` 10, `AlertSeconds` 1, `TimeoutSeconds`, `Enabled`); above the limit an `ALERT:` error line at most once an hour, the gauge `clock.offset_seconds` (meter `Iron.Clock`) and the readiness check `clock` Degraded (TF-065 AC3, ADR-0050); an unreachable server is a warning |
| `Iron.Kernel.Telemetry.Heartbeat` | `AddIronHeartbeat()`: the worker's `worker.heartbeat` span and log line at a fixed interval; `IronActivitySources` for Foundation's own spans |

What a host gets:

- Traces for every request (ASP.NET Core), every outgoing HTTP call and every PostgreSQL command (the `Npgsql` source), exported over OTLP to the collector. Exceptions are recorded on the span.
- Metrics from ASP.NET Core, HttpClient, Npgsql and the runtime, exported the same way; the Grafana dashboard reads them as `http_server_request_duration_seconds`.
- Logs from `ILogger` with the trace id attached, exported to Loki through the collector; the console provider stays for `dotnet run`.
- Resource attributes `service.name`, `service.namespace=foundation`, `service.version`, `deployment.environment.name` and `tenant.id`.
- The exporter endpoint comes from `OTEL_EXPORTER_OTLP_ENDPOINT` (default `localhost:4317`, the laptop collector; `http://otel-collector:4317` inside Compose). If the collector is down, requests still complete; spans are batched and dropped after the queue fills.

Personal data (TF-069, ADR-0050): the scrubbing rule also masks, with `[masked]`, any text shaped like an email address, and the whole value of a log property whose last word is Name, Email, Mail, Phone, Mobile, Address, Street, Postcode or Birthday (`DisplayName`, `email`, `home_address`), where the message shows it too; on spans, the person's own attributes (`enduser.*`, `user.*`). Log identifiers, never people: a property that holds a URL is `Url`, not `Address`. Loki keeps lines 30 days; the audit event is the record of an action, and no log tool reaches it.

Tests: `tests/Kernel/Iron.Kernel.Telemetry.Tests`.
