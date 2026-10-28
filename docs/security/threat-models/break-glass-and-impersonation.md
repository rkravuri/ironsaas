# Threat model: break-glass and impersonation

- **Modules:** identity, tenancy
- **Endpoints:** `POST /auth/sign-in-link`
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

The tenant's trust that the platform's operators reach its unit only when the tenant allows it or an emergency
justifies it, and that every such visit is seen: the operator's one-time sign-in links, the break-glass account, a
tenant's emergency accounts, and the second cookie that carries an impersonation.

## Who can reach it

Operators, through the control plane with their own token; a second operator, who approves break-glass within 30
minutes; whoever holds a one-time link, at `/auth/sign-in-link`; a tenant's emergency accounts, at the ordinary sign-in.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A stolen sign-in link. | One use, a short life, its code in the URL's fragment, given once to the operator who asked and never in a job's output, a log or a trace. | TF-025, TF-026 |
| Spoofing | One operator opening emergency access alone. | A ticket, a reason and a second operator within 30 minutes; a fresh token for each step. | TF-026, TF-181 |
| Tampering | An operator changing what they did in the unit. | The tenant's sealed audit chain, anchored in the control plane and verified daily. | TF-061 |
| Repudiation | Denying an impersonation or a break-glass action. | Both names on every audit event of an impersonation; break-glass ends with a review of its every action; both planes record the request. | TF-025, TF-026 |
| Information disclosure | Impersonating without the person knowing. | The tenant decides whether operators may impersonate; the person sees it. | TF-025 |
| Denial of service | Flooding the sign-in link. | The API's limit per address before sign-in; a code is a 256-bit secret valid for 15 minutes. | TF-072 |
| Elevation of privilege | Using an impersonation or a break-glass account for the most sensitive acts. | Impersonation never steps up, signs or approves a held change; break-glass leaves critical permissions to the tenant's own administrators; emergency accounts always need a second factor and raise an alert at sign-in. | TF-025, TF-026, TF-068 |

## What remains

- The email to the person impersonated waits for S4 (TF-019, TF-025).
- Break-glass's review is a record in the control plane and an event in the tenant's trail until S5's task engine
  makes it a task.
- Alerts on break-glass use and emergency sign-ins with thresholds are T10b's (TF-079).
