# Slice S3: trust and compliance

The third slice (PRD section 7): MFA, single sign-on with OpenID Connect, the sessions console, password policy and lockout, the audit log with its hash chain, change history, secrets from the store, security headers, rate limits and the vulnerability pipeline. S3 is built with Claude Code from `docs/prompts/S3.md` (ADR-0023), in the design language of ADR-0024. This file is the skeleton S2-close left: the requirements, what earlier slices built towards them, and an empty task table. The design session writes what S3 proves, its design and its tasks.

## 1. Before S3 starts

- The Business PRD and the product PRDs are split in the design session before any business slice (ADR-0029). S3 is kernel work and does not wait for the split, but its tasks name modules by layer (`Iron.Kernel.<Module>`), and the PRD's requirement texts still carry the S1 module names until the split renames them.
- The design session settles the requirements the PRD lists in two slices: SAML (TF-015), SCIM (TF-016) and API keys (TF-024) are in S3's range and in S7's description; data retention (TF-066) is in S3's range and in S6's retention jobs.
- What S2 carried into S3 is in the S2 plan's closing notes (`docs/slices/S2-access-control-organisations-and-design.md`, section 7); the items that are S3 requirements are named in the table below.

## 2. What S3 proves

To be written in the design session.

## 3. Requirement coverage

| ID | Requirement | Priority | Built before S3 | Notes for the design session |
|---|---|---|---|---|
| TF-012 | Password policy, history and lockout | Must | Argon2id, the fixed failure message and the count of failed attempts (S1) | the lockout also bounds the smoke's and the e2e's sign-ins |
| TF-013 | Multi-factor authentication | Must | | |
| TF-014 | Single sign-on with OpenID Connect | Must | the in-house client credentials grant (ADR-0015); OpenIddict deferred to the authorization code flow | |
| TF-015 | Single sign-on with SAML 2.0 | Should | | also in S7's description |
| TF-016 | Automatic user provisioning (SCIM 2.0) | Should | | also in S7's description |
| TF-017 | Password recovery and change | Must | | an operator needs it when a tenant's administrator password is lost; a store healed after a loss leaves the administrator without one |
| TF-018 | User invitations | Must | invitation and acceptance (S1); the validity as a unit setting (A6) | the invitation by email is S4's notifications |
| TF-019 | Account security events to the user | Should | | needs S4's notifications for the email |
| TF-022 | Session management console | Must | server-side sessions (S1); Sign out in My profile ends the session in every tab of the browser (D4) | "Sign out everywhere" waits for it |
| TF-023 | Service accounts and client credentials | Must | the client credentials grant with 15-minute tokens (S1) | |
| TF-024 | API keys for simple integrations | Should | the `ApiKey` principal type (S1) | also in S7's description |
| TF-025 | Impersonation with attribution | Must | acting for a delegator with both names in the audit (A7, ADR-0037) | a different thing from delegation; reuse the audit's `on_behalf_of_id` |
| TF-026 | Break-glass access | Should | | |
| TF-027 | Device and login risk signals | Could | | |
| TF-028 | Consent and terms acceptance | Should | | |
| TF-029 | Logout and revocation | Must | logout ends the session and the old cookie is refused (S1, the smoke proves it) | token revocation and refresh rotation |
| TF-061 | Audit integrity chain | Must | the append-only trail, the app role cannot change or delete it (S1, ADR-0013) | |
| TF-062 | Change history (field level) | Must | the history panel in the design system (D3); settings and role changes keep before and after in their events | |
| TF-063 | Electronic signatures | Must | | |
| TF-064 | Audit search, export and retention | Must | the audit list endpoint (S1) | |
| TF-065 | Time source | Must | time through `TimeProvider` everywhere, enforced by the build (ADR-0007) | |
| TF-066 | Data retention and legal hold | Should | | also S6's retention jobs |
| TF-067 | Compliance evidence exports | Must | | |
| TF-068 | Approval of sensitive configuration changes | Should | | the approval engine is S5 |
| TF-069 | Operational logs separate from audit | Must | logs to Loki through OpenTelemetry, the audit in the database (S1, ADR-0010) | |
| TF-071 | Input validation and output encoding | Must | validation with FluentValidation on every body (ADR-0009) | |
| TF-072 | Rate limiting and anti-automation | Must | | every refused request is audited, so a client that keeps calling fills the trail until this lands |
| TF-073 | Secrets management | Must | secrets from the store at start, one secret per unit (S1, ADR-0016); the store persists (ADR-0028); settings that are secret references (A6) | |
| TF-074 | Encryption at rest and in transit | Must | TLS at the ingress on the laptop (S1) | |
| TF-075 | Key management and rotation | Must | the unit's signing key from the store (S1) | key rotation |
| TF-076 | Vulnerability and dependency management | Must | Dependabot, Trivy on the dependencies and the images, gitleaks (S1, ADR-0022); green again in S2-close | |
| TF-077 | Secure development lifecycle | Must | the definition of done, warnings as errors, the build checks | |
| TF-078 | Anti-tampering of files and uploads | Must | | virus scanning of files is S4 |
| TF-079 | Security monitoring and incident response | Must | denial and login events in the audit; the entitlement, flag and settings alerts (S2) | |
| TF-218 | Validation support for regulated tenants | Must | | |
| TF-219 | Signature manifestation and audit trail review | Must | | with TF-063 |

Continuing into S3 from earlier slices: TF-021 (the concurrent session limit), TF-020 (refresh rotation, key rotation, OpenIddict) and TF-070 (CSP reports), each in its slice plan's coverage table.

## 4. Task breakdown

| Task | Name | Scope | Done when | State |
|---|---|---|---|---|
| | | | | |

The design session writes the tasks into this table and into `docs/prompts/S3.md`.

## 5. How we build

As in S2: each task is one Claude Code session from its section in `docs/prompts/S3.md` (ADR-0023), on its own branch from main, never stacked. The result is reviewed in the design session from the diff and the test output; fixes go back as the next prompt. A task is done when its "done when" holds on the laptop, its tests and documentation are in, and its ADRs are written. The State column is updated as tasks land.
