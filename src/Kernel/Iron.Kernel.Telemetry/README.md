# Iron.Kernel.Telemetry

Observable by default (P6, TF-130, ADR-0010). No schema.

| Namespace | What is in it |
|---|---|
| `Iron.Kernel.Telemetry` | `AddIronTelemetry(serviceName)` on the host builder: OpenTelemetry traces, metrics and logs with the service name, the environment and the tenant on every signal; `UseIronTelemetry()` for the correlation middleware; `TelemetryOptions` |
| `Iron.Kernel.Telemetry.Correlation` | `CorrelationMiddleware` (fixes the trace id as the request's correlation id and returns it in `X-Correlation-Id`) and `RequestCorrelation.Get(HttpContext)` |
| `Iron.Kernel.Telemetry.Health` | `AddIronHealth()` and `MapIronHealth()`: `/health/live` and `/health/ready`, with checks tagged `ready`; `HealthProbe`, the `health` command container runtimes use as HEALTHCHECK |
| `Iron.Kernel.Telemetry.Scrubbing` | The scrubbing rule for invitation and recovery links (ADR-0046), the second line behind their never being written: `LinkScrubber`, `ScrubbingLoggerProvider`, which `AddIronTelemetry` wraps around every log provider (message and values), and `ScrubbingActivityProcessor` with the request's enrichment (a span's name and tags) |
| `Iron.Kernel.Telemetry.Clock` | `AddIronClockCheck()`: each process measures its clock against a time server by SNTP (`Telemetry:Clock`: `Server` `pool.ntp.org`, `Port`, `IntervalMinutes` 10, `AlertSeconds` 1, `TimeoutSeconds`, `Enabled`); above the limit an `ALERT:` error line at most once an hour, the gauge `clock.offset_seconds` (meter `Iron.Clock`) and the readiness check `clock` Degraded (TF-065 AC3, ADR-0050); an unreachable server is a warning |
| `Iron.Kernel.Telemetry.Export` | `CountedBatchExportProcessor`, the trace exporter's queue (TF-130 AC4, ADR-0056), and `TraceExportOptions` (`Telemetry:Traces`: `QueueSize` 2048, `BatchSize` 512, `DelayMilliseconds` 5000, `ExportTimeoutMilliseconds` 10000, the SDK's defaults); the meter `Iron.Telemetry` with `telemetry.spans_exported`, `telemetry.spans_dropped` by `reason` (`queue_full`, `export_failed`) and the gauge `telemetry.spans_queued` |
| `Iron.Kernel.Telemetry.Heartbeat` | `AddIronHeartbeat()`: the worker's `worker.heartbeat` span and log line at a fixed interval; `IronActivitySources` for Foundation's own spans |

What a host gets:

- Traces for every request (ASP.NET Core), every outgoing HTTP call and every PostgreSQL command (the `Npgsql` source), exported over OTLP to the collector. Exceptions are recorded on the span.
- Metrics from ASP.NET Core, HttpClient, Npgsql and the runtime, exported the same way; the Grafana dashboard reads them as `http_server_request_duration_seconds`.
- Logs from `ILogger` with the trace id attached, exported to Loki through the collector; the console provider stays for `dotnet run`.
- Resource attributes `service.name`, `service.namespace=foundation`, `service.version`, `deployment.environment.name` and `tenant.id`.
- The exporter endpoint comes from `OTEL_EXPORTER_OTLP_ENDPOINT` (default `localhost:4317`, the laptop collector; `http://otel-collector:4317` inside Compose), or from `Telemetry:Endpoint` when set (a gRPC address for every signal; the tests that start their own collector set it). Each signal has its exporter: metrics and logs the SDK's, traces `CountedBatchExportProcessor` (ADR-0056), since `UseOtlpExporter` cannot be mixed with a processor of our own.
- When the collector is down or hangs, requests still complete and never wait (TF-130 AC4): a finished span only goes on a bounded queue, which a thread of the processor's own sends in batches. A batch whose export fails or times out is dropped, the queue fills meanwhile, and a span beyond it is dropped at once; every one is counted in `telemetry.spans_dropped` with its reason, which Prometheus shows once the collector is back, the counter being cumulative. `LostSpanTests` pauses and stops the collector the laptop stack pins, and starts a host with no collector at all.

Personal data (TF-069, ADR-0050): the scrubbing rule also masks, with `[masked]`, any text shaped like an email address, and the whole value of a log property whose last word is Name, Email, Mail, Phone, Mobile, Address, Street, Postcode or Birthday (`DisplayName`, `email`, `home_address`), where the message shows it too; on spans, the person's own attributes (`enduser.*`, `user.*`). Log identifiers, never people: a property that holds a URL is `Url`, not `Address`. Loki keeps lines 30 days; the audit event is the record of an action, and no log tool reaches it.

Tests: `tests/Kernel/Iron.Kernel.Telemetry.Tests`.

Secrets (TF-073 AC3, ADR-0054): the scrubbing rule also replaces, with `[redacted]`, a connection string's password, account key and shared access signature, a signature or token in a query string, a bearer token, a JSON Web Token, a private key in PEM, a store token, and the value of anything named a password, a secret, a key or a token. A logged exception whose text holds one is passed on as `ScrubbedException`, its message, stack trace and whole text scrubbed and its original type named, to the console and to OpenTelemetry; the ASP.NET Core instrumentation records a request's exception on its span through `ScrubbingActivityProcessor.RecordException`, as that stand-in, since a span's events cannot be changed once added.
