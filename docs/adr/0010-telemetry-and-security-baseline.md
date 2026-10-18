# ADR-0010: Telemetry and security baseline

**Status:** Accepted
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

P6 (observable by default) and TF-130 ask for one correlation id and one standard, OpenTelemetry, for traces, metrics and logs, with the tenant on everything. P5 and TF-070 ask for secure defaults on the web surface: security headers, hardened cookies and cross-origin access only for configured origins. Every host needs both, so they are built once.

## Decision

- OpenTelemetry through the .NET SDK and hosting integration: traces (ASP.NET Core, HttpClient, the `Npgsql` activity source), metrics (ASP.NET Core, HttpClient, `Npgsql`, `System.Runtime`) and logs, exported together over OTLP to the collector with `UseOtlpExporter`. The endpoint comes from the standard `OTEL_EXPORTER_OTLP_ENDPOINT` variable; the laptop collector is the default.
- Logging stays on `Microsoft.Extensions.Logging` with the OpenTelemetry logging provider; Serilog, named in PRD section 6, is not used. Reason: the built-in logger already produces structured logs with scopes and the trace id, the OTLP exporter carries them to Loki, and one dependency family less is simpler. PII scrubbing (TF-136) becomes an OpenTelemetry log processor in S4. Serilog can still be added later without touching callers, because code only sees `ILogger`.
- Resource attributes on every signal: `service.name` (`iron-api`, `iron-worker`, `iron-control-plane`, `iron-fleet`), `service.namespace=foundation`, `service.version`, `deployment.environment.name`, `tenant.id` (`control` for the control plane, `unbound` until T7 binds the tenant).
- The correlation id is the W3C trace id of the request (ADR-0007). `CorrelationMiddleware` runs first, fixes it for the request, returns it in `X-Correlation-Id`, and starts a trace when none exists so the id is stable even without a collector. Problem details and the exception handler read the same fixed id.
- Health: `/health/live` answers when the process runs; `/health/ready` runs the checks tagged `ready` (the database from T6, storage and secrets from T12) and answers 503 with a JSON body that names the failing check. Health traces stay on during S1; probe filtering is tuned when many units run (S6).
- Security headers on every response, including errors: content security policy without inline scripts or styles (configurable, with path exemptions for the API browser in Development), `Strict-Transport-Security` on https, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`, `Cross-Origin-Opener-Policy` and `Cross-Origin-Resource-Policy: same-origin`, and `Cache-Control: no-store` under `/api`.
- Cookies are `Secure`, `HttpOnly` and `SameSite=Strict` through the cookie policy middleware. Browsers treat `localhost` and `*.localhost` as secure, so the laptop works over http.
- CORS: no cross-origin access unless origins are configured (`Security:Cors:AllowedOrigins`); the shell is same-origin and needs none. Configured origins get credentials and see `X-Correlation-Id`.

## Options considered

- Serilog with the OpenTelemetry sink (PRD section 6). Not chosen for S1, see above; revisit if the built-in logger lacks something the team needs.
- A client-supplied correlation id header. Rejected: the trace id already joins everything; clients that need to correlate use the W3C `traceparent` header, which the instrumentation honours.
- Filtering health probes out of traces from the start. Deferred: during S1 the readiness trace is how we see database latency.
- A third-party header library. Rejected: ten header lines need no package.

## Consequences

- Easier: every host is observable and hardened with two calls each; one id to quote to support; the Grafana dashboard fills on the first request.
- Harder: the strict content security policy means the shell must ship without inline scripts or styles (T15), which Vite and Mantine support.
- Revisit: PII scrubbing processor and log sampling (S4), probe filtering and SLO dashboards (S6), CSP reporting and rate limits (S3).
