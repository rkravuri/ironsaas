# Iron.Kernel.Security

Secure by default for the web surface (P5, TF-070, ADR-0010). No schema.

| Type | What it does |
|---|---|
| `AddIronSecurity(configuration)` and `UseIronSecurity()` | Headers on every response, hardened cookies, CORS only for configured origins |
| `SecurityOptions` | The `Security` configuration section: `Cors:AllowedOrigins`, `Headers:ContentSecurityPolicy`, `Headers:CspExemptPathPrefixes`, `Headers:EnableHsts`, `Cookies:SecurePolicy` |
| `SecurityHeadersMiddleware` | `Content-Security-Policy` (no inline scripts or styles), `Strict-Transport-Security` on https, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`, cross-origin opener and resource policies, `Cache-Control: no-store` under `/api` and `/auth` |

Cookies are `Secure`, `HttpOnly` and `SameSite=Strict`. Cross-origin requests are blocked unless the origin is listed; the shell is same-origin and needs no entry. Later slices add the secret store interface (T12) and rate limits, the CSP report endpoint and the vulnerability process (S3).

Tests: `tests/Kernel/Iron.Kernel.Security.Tests`.

## Secrets (T12, ADR-0016)

`Iron.Kernel.Security.Secrets`: `ISecretStore` with `OpenBaoSecretStore` (KV version 2 over HTTP), `AddIronSecrets()` on the configuration builder (the unit's secret becomes configuration at start), `AddIronSecretStore()` (the store and its readiness check when `Secrets:Address` and `Secrets:Token` are configured). Settings: `Secrets:Address`, `Secrets:Token`, `Secrets:Mount` (`secret`), `Secrets:Path` (`tenants/<id>`).

One-time places (ADR-0046): `ISecretStore.WriteOnceAsync` writes a path only if nothing was ever written there (check-and-set 0) and has the store delete it after a lifetime (`delete_version_after`); `DeleteAsync` removes a path with every version. `ISecretStoreAdmin.CreateDepositPolicyAsync` makes a policy that may write at a path pattern and not read. `StoreOperatorHandover` is `IOperatorHandover` (Iron.Kernel) for an image command the fleet gives a place: the `Handover` section, `Path` and `Token`, set only by the fleet. Without them a process hands nothing over (`NoOperatorHandover`).

Headers (TF-070 AC1, ADR-0053): every answer carries the content security policy (no `unsafe-inline`; `report-uri /api/v1/csp-reports` and `report-to csp`, with `Reporting-Endpoints`), HSTS (sent even when the ingress speaks plain HTTP to the host), frame denial, `nosniff`, the referrer policy and the permissions policy. Cookies are Secure, HttpOnly and SameSite=Strict by policy, whatever the code that writes them asks. Cross-origin calls are the Protection module's: the operator's `Security:Cors:AllowedOrigins` and each tenant's own list.

Rich text (TF-071 AC2, ADR-0053): `Iron.Kernel.Security.Text.RichText` holds HTML cleaned by HtmlSanitizer's allow-list as it is made; its JSON converter cleans a request's field as it binds and refuses more than 256 KB unread. The allow-list (`RichTextSanitizer`) keeps paragraphs, line breaks, emphasis, lists, quotes, code, headings from the second level, rules, tables, and links to `http`, `https` and `mailto` with `rel="noopener noreferrer nofollow"`; everything else goes, a script's or a style's text with it. A field of this type declares `RuleFor(x => x.Body).MaximumLength(n)`, which the request limit check reads.

## Keys (TF-074, TF-075, ADR-0054)

`Iron.Kernel.Security.Keys`: the key service the kernel vocabulary declares (`IKeyService`: encrypt, decrypt, re-wrap, hash, sign, versions; every value it makes carries its version, `<service>:v<n>:<base64>`) and what the fleet does with keys (`IKeyAdministration`: make, describe, rotate, retire).

| Type | What it does |
|---|---|
| `TransitKeyService` | OpenBao's transit engine over its HTTP API (`Secrets:TransitMount`, `transit`), batch calls for lists; the keys stay in the store. A retired version's refusal is `KeyVersionRetiredException`, any other refusal `SecretStoreException` |
| `LocalKeyService` | The same rules in the process, for the tests and a host run from source without a store: every key derived from one material (`Keys:Local:Material`, or the file at `Keys:Local:MaterialPath`, written on first start) by HKDF with its name and version, signing keys as a private scalar, so hosts that share a material agree. Never for a unit with a store |
| `KeyOptions` | The `Keys` section: `Prefix`, the unit's key family (`tenant-<slug>`, `control`), and `Local` |
| `AddIronKeys(configuration)` | Called by `AddIronSecretStore`: the transit engine with a store, the process's keys without one; a test or module that registered one first keeps it |

The store's administration also writes a policy of any rules (`CreatePolicyAsync`, `StorePolicyRule`), grants a token the use of named keys (`KeyGrant`, `KeyUses`: encrypt, decrypt, re-wrap, hash, sign, read), makes a token role that allows only some policies (`EnsureTokenRoleAsync`), makes tokens through it when `Secrets:TokenRole` is set (the fleet's `tenant-units`), and says whether a token still opens the store (`IsTokenValidAsync`).

The database password (TF-073 AC2): `StoreDatabaseCredentials` is the kernel's `IDatabaseCredentials` when a store is configured: the unit secret's password, read again after `Database:PasswordRefreshSeconds` (60) or at once after a refusal, for the login the process connects as only. The Data library's data source asks it at each new connection.
