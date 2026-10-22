# Slice S3: trust and compliance

The third slice (PRD section 7): MFA, single sign-on with OpenID Connect, the sessions console, password policy and lockout, the audit log with its hash chain, change history, secrets from the store, security headers, rate limits and the vulnerability pipeline. S3 is built with Claude Code from `docs/prompts/S3.md` (ADR-0023), in the design language of ADR-0024. This file is the skeleton S2-close left: the requirements, what earlier slices built towards them, and an empty task table. The design session writes what S3 proves, its design and its tasks.

## 1. Before S3 starts

- The requirements are split into one document per layer and product (ADR-0041, task PRD-split). S3 is kernel work and names modules by layer (`Iron.Kernel.<Module>`), as the technical PRD's section 2.1 now does. S3's tests name what they prove by identifier and criterion (TF-218), in every PRD's identifiers.
- The design session settles the requirements the PRD lists in two slices: SAML (TF-015), SCIM (TF-016) and API keys (TF-024) are in S3's range and in S7's description; data retention (TF-066) is in S3's range and in S6's retention jobs.
- What S2 carried into S3 is in the S2 plan's closing notes (`docs/slices/S2-access-control-organisations-and-design.md`, section 7); the items that are S3 requirements are named in the table below.

## 2. What S3 proves

To be written in the design session.

## 3. Requirement coverage

| ID | Requirement | Priority | Built before S3 | Notes for the design session |
|---|---|---|---|---|
| TF-012 | Password policy, history and lockout | Must | Argon2id, the fixed failure message and the count of failed attempts (S1) | T2 (ADR-0046): the policy as tenant settings, history and lockout. AC2's email of the lockout is S4's, with notifications: the lockout is recorded and shown in My profile until then |
| TF-013 | Multi-factor authentication | Must | | T3 (ADR-0047): authenticator apps and passkeys, recovery codes, the policy as tenant settings, the cooling period and the lock, the administrator's and the operator's reset. The fallback by email or SMS is S4's, an exception when S3 closes |
| TF-014 | Single sign-on with OpenID Connect | Must | the in-house client credentials grant (ADR-0015); OpenIddict deferred to the authorization code flow | |
| TF-015 | Single sign-on with SAML 2.0 | Should | | also in S7's description |
| TF-016 | Automatic user provisioning (SCIM 2.0) | Should | | also in S7's description |
| TF-017 | Password recovery and change | Must | | T2 (ADR-0046): a one-time, 15-minute link issued by an administrator or by the operator through the control plane, and the change with the current one. The link to the verified email, which the person asks for themself, is S4's |
| TF-018 | User invitations | Must | invitation and acceptance (S1); the validity as a unit setting (A6) | the invitation by email is S4's notifications |
| TF-019 | Account security events to the user | Should | | T2 records the events and shows them in My profile, each also audited. AC1 (the email of a new device's login within a minute), AC2 (the email's link to the active sessions with end-session actions, which T5's sessions console brings) and AC3 (a failed send still audited) are about the email: S4's, as exceptions when S3 closes |
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
| TF-248 | Step-up re-authentication | Must | | T3 (ADR-0047), new: a recent authentication for the high-risk actions, one mechanism in the kernel, the API and the shell; T5's break-glass, T7's signatures and T8's second approvals reuse it |

Continuing into S3 from earlier slices: TF-021 (the concurrent session limit), TF-020 (refresh rotation, key rotation, OpenIddict) and TF-070 (CSP reports), each in its slice plan's coverage table.

## 4. Task breakdown

From `docs/prompts/S3.md`, where each task has its section: T1 complete, T2 to T10 outlines that the design session sharpens before each starts.

| Task | What it builds | Requirements | State |
|---|---|---|---|
| T1 | Traceability: every test names what it proves, a generated matrix with results, a reliable check sequence | TF-218 (part) | done (main, df1e35e) |
| T2 | Passwords: policy, history, lockout, recovery and change; account security events | TF-012, TF-017, TF-019 | done (main, 902cd3d) |
| T3 | MFA and step-up re-authentication | TF-013, TF-248 | done (main, e1752cc) |
| T4 | Single sign-on with OpenID Connect | TF-014 | in review (slice/s3-t4) |
| T5 | Sessions console, logout and revocation, impersonation, break-glass, terms acceptance | TF-022, TF-025, TF-026, TF-028, TF-029 | outline |
| T6 | Audit integrity chain, time source, audit search, export and retention, legal hold, operational logs apart | TF-061, TF-064 to TF-066, TF-069 | outline |
| T7 | Field-level change history, electronic signatures, signature manifestation, audit-trail review | TF-062, TF-063, TF-219 | outline |
| T8 | Evidence exports, second approval for sensitive changes, validation pack per release | TF-067, TF-068, TF-218 | outline |
| T9 | Hardening: input validation and output encoding review, rate limits and anti-automation (the "Use your company login" answer included, T4's review), encryption in transit, key management and rotation | TF-070 to TF-075 | outline |
| T10 | Secure development lifecycle, vulnerability management, security monitoring and incident response | TF-076, TF-077, TF-079 | outline |
| S3-close | Smoke steps, documentation, closing notes | | not started |

Outside S3, as the technical PRD's section 7 says: SAML (TF-015), SCIM (TF-016) and API keys (TF-024) in S7; anti-tampering of uploads (TF-078) in S4; device and login risk signals (TF-027) later.

### Later items folded into the tasks

STATUS's later items that belong to S3, each in the task it fits (T1 decided the fold; the design session moves an item when it sharpens a task):

- **T1**: the shell's page tests that timed out beside the stack straight after `make test` (measured and settled, ADR-0044); the tests tagged with every PRD's identifiers (TF-218).
- **T2**: a password reset an operator can use when a tenant's administrator password is lost, which also lets the smoke and the browser tests set a password again; an API that disables a person, so that each browser test run and each smoke run stop leaving an inactive unit with its people behind; the last-administrator guard counting a disabled or locked administrator, since T2 brings the locked state.
- **T5**: "Sign out everywhere" in the sessions console; a response header with the session's idle end, so the shell's estimate is exact when a tenant shortens the limits.
- **T8**, before it starts: the release's "Traceability matrix" job showed no conclusion in run 39620828352; check that it runs and keeps its artifact (T3's review).
- **T9**: rate limits that bound a client calling a refused endpoint, each refusal now writing `entitlements.access_denied` to the audit trail; the shell's web server answering 404, not its page, for `/api/` and `/health/` paths (T3's review); the factor key's rotation (ADR-0047); rate limits that cover the sign-in's "Use your company login" answer, which tells an identifier that signs in only through a provider (T4's review, ADR-0048).
- **T10**: Dependabot's cooldown and the two packages it is told to leave, under the dependency policy with its fix times; the chiseled images' Kerberos probe message, with the images' SBOM; a system role's new permissions from the seed shown in the conflicts report and raised as a privilege grant, which TF-079's alerts cover; the counter of lost spans (TF-130 AC4), which the S1 plan listed as built and was not (T1's review, 2026-10-21).

The other later items belong to the business and suite slices, S4 (notifications and the escalation messages), S5 (workflow) or the slice of their own requirement, and stay in STATUS.

## 5. How we build

As in S2: each task is one Claude Code session from its section in `docs/prompts/S3.md` (ADR-0023), on its own branch from main, never stacked. The result is reviewed in the design session from the diff and the test output; fixes go back as the next prompt. A task is done when its "done when" holds on the laptop, its tests and documentation are in, and its ADRs are written. The State column is updated as tasks land.
