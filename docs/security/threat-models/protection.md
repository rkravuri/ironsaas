# Threat model: protection of the web surface

- **Modules:** protection
- **Endpoints:** `GET /auth/challenge`, `POST /api/v1/csp-reports`
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

The API's availability and its anonymous surface against automation: the rate limits, the login's challenge, the
origins a tenant lets call its API from a browser, and the channel through which browsers report policy violations.

## Who can reach it

Anyone, for the challenge and the reports; a tenant's administrators, who set the limits within the operator's bounds
and list the allowed origins; operators, who set the bounds.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A bot passing for a person after failed sign-ins. | After five failures the login asks for a proof of work the server issued, checked once, with growing work; no external CAPTCHA host. | TF-072 |
| Tampering | Forged violation reports. | Reports are counted and sampled for reading, never acted on; at most 64 KB each. | TF-070 |
| Repudiation | Denying a burst of refused calls. | A rate-limit refusal is audited at low severity, once per window and subject, and counted per limit. | TF-072 |
| Information disclosure | Another site reading the API's answers in a browser. | CORS answers only the tenant's listed origins, empty by default, HTTPS except this machine. | TF-070 |
| Information disclosure | A report carrying a page's query string. | Kept without query strings, the latest 100 only. | TF-070 |
| Denial of service | Floods of calls or reports. | Sign-in 10 a minute per address, recovery 5 an hour per account, the API 600 a minute per principal, exports 20 an hour, reports 60 a minute per address, each 429 with `Retry-After`; declared limits on every request field. | TF-072, TF-071 |
| Elevation of privilege | A tenant removing its own limits. | A tenant moves a limit only within the operator's bounds. | TF-072 |

## What remains

- The counters live in each API process; with several replicas per unit (S6) they move to a shared store, and a
  restart clears them until then.
- Alerts on sustained refusals are T10b's (TF-079).
