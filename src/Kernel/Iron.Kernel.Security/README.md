# Iron.Security

Secure by default for the web surface (P5, TF-070, ADR-0010). No schema.

| Type | What it does |
|---|---|
| `AddIronSecurity(configuration)` and `UseIronSecurity()` | Headers on every response, hardened cookies, CORS only for configured origins |
| `SecurityOptions` | The `Security` configuration section: `Cors:AllowedOrigins`, `Headers:ContentSecurityPolicy`, `Headers:CspExemptPathPrefixes`, `Headers:EnableHsts`, `Cookies:SecurePolicy` |
| `SecurityHeadersMiddleware` | `Content-Security-Policy` (no inline scripts or styles), `Strict-Transport-Security` on https, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`, cross-origin opener and resource policies, `Cache-Control: no-store` under `/api` and `/auth` |

Cookies are `Secure`, `HttpOnly` and `SameSite=Strict`. Cross-origin requests are blocked unless the origin is listed; the shell is same-origin and needs no entry. Later slices add the secret store interface (T12) and rate limits, the CSP report endpoint and the vulnerability process (S3).

Tests: `tests/Iron.Security.Tests`.

## Secrets (T12, ADR-0016)

`Iron.Security.Secrets`: `ISecretStore` with `OpenBaoSecretStore` (KV version 2 over HTTP), `AddIronSecrets()` on the configuration builder (the unit's secret becomes configuration at start), `AddIronSecretStore()` (the store and its readiness check when `Secrets:Address` and `Secrets:Token` are configured). Settings: `Secrets:Address`, `Secrets:Token`, `Secrets:Mount` (`secret`), `Secrets:Path` (`tenants/<id>`).
