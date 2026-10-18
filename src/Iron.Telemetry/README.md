# Iron.Telemetry

Observable by default (P6, TF-130, ADR-0010). No schema.

| Namespace | What is in it |
|---|---|
| `Iron.Telemetry` | `AddIronTelemetry(serviceName)` on the host builder: OpenTelemetry traces, metrics and logs with the service name, the environment and the tenant on every signal; `UseIronTelemetry()` for the correlation middleware; `TelemetryOptions` |
| `Iron.Telemetry.Correlation` | `CorrelationMiddleware` (fixes the trace id as the request's correlation id and returns it in `X-Correlation-Id`) and `RequestCorrelation.Get(HttpContext)` |
| `Iron.Telemetry.Health` | `AddIronHealth()` and `MapIronHealth()`: `/health/live` and `/health/ready`, with checks tagged `ready`; `HealthProbe`, the `health` command container runtimes use as HEALTHCHECK |
| `Iron.Telemetry.Heartbeat` | `AddIronHeartbeat()`: the worker's `worker.heartbeat` span and log line at a fixed interval; `IronActivitySources` for Foundation's own spans |

What a host gets:

- Traces for every request (ASP.NET Core), every outgoing HTTP call and every PostgreSQL command (the `Npgsql` source), exported over OTLP to the collector. Exceptions are recorded on the span.
- Metrics from ASP.NET Core, HttpClient, Npgsql and the runtime, exported the same way; the Grafana dashboard reads them as `http_server_request_duration_seconds`.
- Logs from `ILogger` with the trace id attached, exported to Loki through the collector; the console provider stays for `dotnet run`.
- Resource attributes `service.name`, `service.namespace=foundation`, `service.version`, `deployment.environment.name` and `tenant.id`.
- The exporter endpoint comes from `OTEL_EXPORTER_OTLP_ENDPOINT` (default `localhost:4317`, the laptop collector; `http://otel-collector:4317` inside Compose). If the collector is down, requests still complete; spans are batched and dropped after the queue fills.

Tests: `tests/Iron.Telemetry.Tests`.
