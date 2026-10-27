# Iron.Kernel.Protection

What protects the web surface beyond the headers (TF-070, TF-072, ADR-0053): cross-origin access only from the tenant's origins, rate limits with the tenant's values within the operator's bounds, the login's proof-of-work challenge after repeated failures, and the report endpoint of the content security policy. It keeps nothing in a database, so it has no schema: its counters live in each process until S6 brings several replicas per unit and a shared store.

Placement: no business meaning, so Kernel. The headers themselves and the cookie policy are the `Iron.Kernel.Security` library's; the rate-limit marker an endpoint carries (`.RateLimited(...)`) and the client's address behind the ingress (`ClientAddress`) are `Iron.Kernel.Api`'s, so every module names its limits without referencing this one.

| Folder | Holds |
|---|---|
| `Contracts/` | `ProtectionSettings` (the origins and the four limits), `RateLimitBounds` (the operator's bounds), `ProtectionPermissions`, `ProtectionAuditEvents`, `ProtectionErrors`, and `ILoginChallenges` with `LoginChallenge`, which Identity asks |
| `Services/` | `RateLimitMiddleware` and `RateLimitGate` (fixed windows per limit, subject and value), `LoginChallenges` (ALTCHA's protocol, written here), `CspReports` (counted, sampled, logged a few a minute), `TenantCorsPolicyProvider`, `ProtectionOptions`, `ProtectionMetrics` |
| `Api/` | `POST /api/v1/csp-reports`, `GET /api/v1/csp-reports`, `GET /auth/challenge` |

Hosts add the module and call `UseIronProtection()` after `UseIronIdentity()`: the CORS check, then the rate limits, with the principal known.

## Rate limits (TF-072)

| Limit | Default | Per | Bounds by default | Endpoints |
|---|---|---|---|---|
| `login` | 10 a minute | address | 3 to 60 | `POST /auth/login` (the "Use your company login" answer included), `POST /connect/token` |
| `recovery` | 5 an hour | account (the route's person or tenant), else address | 1 to 20 | `POST /api/v1/users/{id}/recovery-link`, `POST /api/v1/tenants/{slug}/administrator-recovery`, `POST /auth/recovery` |
| `api` | 600 a minute | principal, else address | 60 to 6000 | every path under `/api`, `/auth` and `/connect` |
| `exports` | 20 an hour | principal | 1 to 200 | audit exports, role export, evidence packs, the validation pack's download |
| `csp_reports` | 60 a minute | address | the operator's | `POST /api/v1/csp-reports` |

A tenant changes its values in Settings (`protection.limits.*`, tenant scope, behind a recent sign-in); a value outside the operator's bounds is refused when saved, and held to them when applied. The operator sets the bounds in configuration: `Protection:Limits:<limit>:Minimum` and `Maximum`. The limiter reads the tenant's value at every request from the settings' cache, so a change applies within 30 seconds; the window's key holds the value, so a new value starts a new window.

A refusal answers 429 with `Retry-After` (seconds), `error_code` `protection.rate_limited` and `retry_after_seconds`; the counter `protection.rate_limited` counts it by limit; the first refusal of a subject in a window writes `protection.rate_limited` to the audit trail at low severity, with the limit, its value, the window, the kind of subject and the path, never the address.

The address is the client's behind the ingress (`ClientAddress`: the last `X-Forwarded-For` entry from a private peer).

## The login's challenge (TF-072 AC4)

After 5 failed sign-ins within 15 minutes from one address or for one login identifier, `POST /auth/login` answers 403 `identity.login.challenge_required` until the request carries a solved challenge: `GET /auth/challenge` gives one (`algorithm`, `challenge`, `max_number`, `salt` with its expiry, `signature`), the client tries each number up to the maximum until the SHA-256 of the salt and the number is the challenge, and sends the answer as ALTCHA's payload (base64 JSON) in the login's `challenge`. The server checks the signature (HMAC-SHA256 with its key), the hash, the expiry (5 minutes) and that the answer was not used before. Each failure from the address past the threshold doubles the next challenge's work, up to sixteen times. T2's lockout applies on top. Nothing is fetched from another host, so the content security policy stays strict.

Configuration (`Protection:Challenge`): `AfterFailures` (5), `WindowMinutes` (15), `MaxNumber` (100 000), `LifetimeMinutes` (5), `Key` (base64; without it each process makes one at start).

## The report endpoint of the content security policy (TF-070 AC3)

Every host's policy names `report-uri /api/v1/csp-reports` and `report-to csp` (with `Reporting-Endpoints: csp="/api/v1/csp-reports"`). The endpoint takes both forms (`application/csp-report`, `application/reports+json`), at most 64 KB, from anyone, within its own limit per address; it counts each report by directive (`protection.csp_reports`), keeps the latest 100 in the process, and writes a few a minute to the log (`CSP violation: ...`). The page is kept without its query. `GET /api/v1/csp-reports` lists the kept ones for `protection.csp_reports.read`.

Configuration (`Protection:CspReports`): `PerMinute` (60), `Sample` (100), `LogPerMinute` (20), `MaxBytes` (65 536).

## Cross-origin access (TF-070 AC2)

`protection.cors.allowed_origins` (tenant, JSON list, at most 20, behind a recent sign-in) names the web origins whose pages may call the tenant's API from a browser: a scheme and a host with an optional port, https, or http for a host on this machine. The operator's `Security:Cors:AllowedOrigins` adds to every tenant's. A listed origin gets the CORS answer with credentials and `X-Correlation-Id` and `Retry-After` exposed; any other gets none, and the browser refuses the page the response.
