# Slice S3: trust and compliance

The third slice (PRD section 7): MFA, single sign-on with OpenID Connect, the sessions console, password policy and lockout, the audit log with its hash chain, change history, secrets from the store, security headers, rate limits and the vulnerability pipeline. S3 was built with Claude Code from `docs/prompts/S3.md` (ADR-0023), in the design language of ADR-0024, from 2026-10-21 to 2026-10-29. S2-close left this file as a skeleton; S3-close wrote what S3 proves, its coverage, its decisions and its closing notes.

## 1. Before S3 started

- The requirements are split into one document per layer and product (ADR-0041, task PRD-split). S3 is kernel work and names modules by layer (`Iron.Kernel.<Module>`), as the technical PRD's section 2.1 now does. S3's tests name what they prove by identifier and criterion (TF-218), in every PRD's identifiers.
- The design session settled the requirements the PRD lists in two slices: SAML (TF-015), SCIM (TF-016) and API keys (TF-024) are S7's; data retention (TF-066) is built in S3 and its jobs over the products' records continue in S6.
- What S2 carried into S3 is in the S2 plan's closing notes (`docs/slices/S2-access-control-organisations-and-design.md`, section 7); the items that are S3 requirements are named in the table below.

## 2. What S3 proves

Each with where the proof is, on 2026-10-29:

- every test names the criteria or the decisions it proves, and the matrix holds S1, S2 and S3 as done slices with every criterion tested or an approved exception with its home: `make trace` on every CI run, and `docs/trace/exceptions.md`;
- a person signs in with a password under the tenant's policy, a second factor or a passkey, or through a company login, and confirms a risky action with a recent sign-in: the Identity tests, the smoke's `login`, `administrator-factor`, `second-factor` and `single-sign-on` steps, the browser tests with Chromium's virtual authenticator and the test provider;
- an administrator sees and ends sessions, and the operator recovers a tenant's administrator, signs in as a person or opens break-glass only with a second operator: the Identity and Fleet tests, the smoke's `recovery`, `end-session` and `revoke-person` steps, and the runbook's drills on echo;
- every change is in a sealed chain, verified from outside the unit, and a signature binds to the version it signs: the Audit and Compliance tests, the smoke's `audit-trail` step, `make verify-audit` on echo, and the Audit README's Python recomputing echo's hashes;
- an auditor gets an evidence pack for a period, sensitive configuration waits for a second administrator, and every release carries its validation pack with its bills of materials: the Compliance, Changes and Fleet tests and the installation record of each unit's update;
- the web surface refuses inline code, unlimited fields, scripts in rich text and SQL from strings, and limits floods: the Protection tests, the analyzers, and the browser test of the policy's reports;
- a unit holds no key, chosen fields are encrypted and found by their exact value, and keys and passwords rotate without a restart: the Keys, Data and Fleet tests and the smoke's `tls` and `key-rotation` steps;
- code reaches main only through a pull request with every check green, the scans fail the build on a high finding, and coverage may not fall: `docs/security/branch-protection.md`, pull requests #16 and #18 and this task's;
- 100 denials in ten minutes become an alert in the tenant, the control plane and Alertmanager, a rule that cannot run is an alert, and an incident runs to its notice and its review: the Monitoring and Tenancy tests, the smoke's `security-alert` step, and one incident run by hand.

At the close: 1209 .NET test cases, 397 Vitest, 152 scans, 92 Python tests, the smoke's 24 steps and 42 browser tests against the stack; thirteen ADRs (0044 to 0056).

## 3. Requirement coverage

What S3 built for each requirement, and where it continues. The criteria S3 added are each in the PRD with a note and the change log, each stating what the code did and a test proves (T1's rule): TF-012 AC4 and AC5, TF-014 AC4 to AC10, TF-017 AC4, TF-022 AC4 and AC5, TF-025 AC4 to AC6, TF-026 AC4, TF-029 AC4, TF-062 AC4, TF-063 AC4 to AC6, TF-064 AC4, TF-071 AC4 and AC5, TF-072 AC4, TF-074 AC4 and AC5, TF-075 AC5, TF-076 AC4 and AC5, TF-219 AC4, and TF-248, new, with six. T1's review also amended TF-030 AC2 and TF-033 AC1 and added criteria to S1's and S2's requirements (TF-030, TF-032 to TF-034, TF-190), which their plans' tables keep.

| ID | Requirement | What S3 built | Continues in |
|---|---|---|---|
| TF-012 | Password policy, history and lockout | The policy as seven tenant settings: length, the common-password check, history, maximum age with its warning, and lockout by login identifier with its threshold, window and duration; one check on every path that sets a password (T2, ADR-0046) | S4 (the lockout's email, a part of AC2) |
| TF-013 | Multi-factor authentication | Authenticator apps and passkeys, ten recovery codes, the policy for everyone or for roles, the cooling period and the lock, the administrator's and the operator's reset (T3, ADR-0047); the secrets under the tenant's data key (T9b) | S4 (one-time codes by email or SMS, the fallback) |
| TF-014 | Single sign-on with OpenID Connect | Providers per tenant with the client secret in the store; the code flow with PKCE, state and nonce; the ID token checked in full; verified emails of allowed domains; linking, just-in-time creation, single sign-on only, roles from groups, trust of the provider's MFA, the connection test (T4, ADR-0048); a provider that cannot be reached said in words (S3-close) | S7 (SAML, TF-015, and SCIM, TF-016) |
| TF-017 | Password recovery and change | One-time 15-minute links from an administrator or from the operator through the control plane, never in a log; the change with the current one, ending the other sessions in the same step (T2, ADR-0046) | S4 (a person's own link to their verified email) |
| TF-018 | User invitations | The invitation's link given once to whoever asked, never in a job's output, a log or a trace (T2's review, ADR-0046); a provider's first sign-in closes an invitation (T4) | S4 (the invitation by email and its resend, AC3) |
| TF-019 | Account security events to the user | The security events recorded, audited and shown in My profile's Security tab (T2, T3, T5) | S4 (the emails, AC1 to AC3) |
| TF-022 | Session management console | A person's sessions in My profile with device, place and method; the console's live sessions and summary; ending a session with a reason; the concurrent limit (T5, ADR-0049) | done |
| TF-023 | Service accounts and client credentials | Built in S1 (ADR-0015); since T9b their tokens are signed by the tenant's signing key with key identifiers, and the key rotates (ADR-0054) | S7 (the address allow-list, AC2, and a secret's overlap, AC3) |
| TF-025 | Impersonation with attribution | A second cookie, both names on every audit event, shown to the person, never a step-up, a signature or an approval; the operator's through the fleet with a one-time link where the tenant allows it (T5, ADR-0049) | S4 (the email to the person impersonated) |
| TF-026 | Break-glass access | A ticket and a second operator within 30 minutes, a `break-glass` account in the unit, the review of its every action; up to two emergency accounts per tenant (T5, ADR-0049) | S5 (the review as a task) |
| TF-028 | Consent and terms acceptance | The platform's and the tenant's documents, a required step per version, declining signs out; `make accept-documents` (T5, ADR-0049) | done |
| TF-029 | Logout and revocation | "Valid after" times for a principal and for the tenant, refusing sessions and tokens within a minute (T5, ADR-0049) | S7 (refresh tokens and their rotation, TF-020 AC1) |
| TF-061 | Audit integrity chain | A per-tenant chain of SHA-256 seals by the unit's worker, anchored in the control plane, verified daily and in full monthly by the fleet with its own role; restore points accepted with a ticket, the chain continuing as a segment (T6, ADR-0050); the control plane's own chain (T9a) | the first Azure deployment (anchors in immutable blob storage) |
| TF-062 | Change history (field level) | Every save of a tracked record one chained audit event with its fields, sensitive values masked and secrets only as changed; the model check names an untracked business record; the History tab (T7, ADR-0051) | the business and suite slices track their records |
| TF-063 | Electronic signatures | Bound by the hash of a snapshot of the version signed; Part 11's components rule proved by Identity; never while impersonating, acting for someone, with a token or as a service account; the policy per document type (T7, ADR-0051) | the business and suite slices sign their documents |
| TF-064 | Audit search, export and retention | Search by time, who, impersonator, record, type and source, and since T10b by target and address; CSV export; the archive after 13 months, still searched; pruning after 7 years (T6, ADR-0050) | done |
| TF-065 | Time source | No client time in a request body, every time in the shell in the person's zone, each process's clock check with its alert (T6, ADR-0050) | done |
| TF-066 | Data retention and legal hold | Retention classes within the operator's limits, the daily job that archives then deletes, legal holds on a record, a master's records or the tenant (T6, ADR-0050) | S6 (the retention jobs over the products' records, and the privacy requests TF-196 to TF-198 with the client address's shorter retention) |
| TF-067 | Compliance evidence exports | Evidence packs per period: each module's tables at the period's end, the manifest, the chain head with its anchor, a one-use link (T8, ADR-0052) | S4 (the pack's PDF) |
| TF-068 | Approval of sensitive configuration changes | Four-eyes on roles, assignments, single sign-on, retention, legal holds, the MFA and password settings and the signing policy; a held change answering 202 (T8, ADR-0052) | S4 (its notices); S5 (governed changes, TF-247) |
| TF-069 | Operational logs separate from audit | Personal data masked in logs, Loki keeping 30 days, no log tool reaching the audit store (T6, ADR-0050) | done |
| TF-071 | Input validation and output encoding | A declared limit for every request field, checked at start; rich text by an allow-list; file names as display text; SQL from strings failing the build (T9a, ADR-0053) | S4 (the first rich-text fields) |
| TF-072 | Rate limiting and anti-automation | Sign-in, recovery, API and export limits with Retry-After, counted and audited; a proof of work after five failures (T9a, ADR-0053) | S6 (the counters in a shared store, with replicas) |
| TF-073 | Secrets management | Separate secrets, policies and store tokens for the control plane's API and the fleet; a rotated database password used without a restart; secrets masked in every log line and span (T9b, ADR-0054) | the first Azure deployment (Key Vault behind `IKeyService`) |
| TF-074 | Encryption at rest and in transit | Chosen fields encrypted with the tenant's data key and found by the keyed hash of their exact value; TLS 1.2 and up at the ingress, transit in the store (T9b, ADR-0054) | the first Azure deployment (TLS inside the network, encryption at rest with customer-managed keys) |
| TF-075 | Key management and rotation | Three keys per tenant in the key service; rotation on schedule and on demand by the key officer, every value moved before the old version is retired (T9b, ADR-0054) | S6 (the control plane's own keys) |
| TF-076 | Vulnerability and dependency management | One gate for dependencies, licences, images, misconfigurations and our rules, with expiring exceptions; a bill of materials per image; the findings register and the monthly report (T10a, ADR-0055) | the first Azure deployment (the yearly independent penetration test) |
| TF-077 | Secure development lifecycle | Pull requests with every check green and main protected; threat models checked by the tests; coverage floors; the second developer's review as PE-1 (T10a, ADR-0055) | PE-1 until a second developer joins |
| TF-079 | Security monitoring and incident response | Rules over the audit trail every minute, a rule that cannot run as an alert, alerts copied to the control plane and sent to Alertmanager, incidents with the 72-hour notice and the review, the runbook (T10b, ADR-0056) | S6 (TF-134: rules over metrics and the on-call channel) |
| TF-218 | Validation support for regulated tenants | Every test naming what it proves, the matrix with results (T1, ADR-0044); release versions, the validation pack, the installation record (T8, ADR-0052); bills of materials in the pack (T10a) | every slice's tests |
| TF-219 | Signature manifestation and audit trail review | The signature block on screen and in print, attachment elsewhere refused by hash, signed audit-trail reviews by month, login identifiers never reused (T7, ADR-0051) | S4 (the block in PDF) |
| TF-248 | Step-up re-authentication | New in T3: a recent authentication for the risky actions, one mechanism in the kernel, the API and the shell; through a provider a true fresh sign-in (T3, T4, ADR-0047, ADR-0048) | done |

Continuing into S3 from earlier slices, each in its own plan's table: TF-020 (token signing keys rotate since T9b, so AC3 is proven; refresh tokens wait for S7), TF-021 AC2 (the concurrent session limit, T5), TF-060 (the control plane's own trail, T9a), TF-070 (the policy's reports, T9a), TF-130 AC4 (the lost spans counted, T10b) and TF-209 AC3 and AC4 (every rule's runbook section, T10b).

Outside S3, as the technical PRD's section 7 now says: SAML (TF-015), SCIM (TF-016) and API keys (TF-024) in S7; anti-tampering of uploads (TF-078) in S4 with the files and their virus scan; device and login risk signals (TF-027, Could) in S7 with them, at S3-close's review; dashboards and alerting (TF-134) in S6, as ADR-0056 says.

## 4. Task breakdown

From `docs/prompts/S3.md`, where each task has its section, sharpened in the design session just before it started (T9 and T10 each split in two).

| Task | What it builds | Requirements | State |
|---|---|---|---|
| T1 | Traceability: every test names what it proves, a generated matrix with results, a reliable check sequence | TF-218 (part) | done (main, df1e35e) |
| T2 | Passwords: policy, history, lockout, recovery and change; account security events | TF-012, TF-017, TF-019 | done (main, 902cd3d) |
| T3 | MFA and step-up re-authentication | TF-013, TF-248 | done (main, e1752cc) |
| T4 | Single sign-on with OpenID Connect | TF-014 | done (main, becf9a8) |
| T5 | Sessions console, logout and revocation, impersonation, break-glass, terms acceptance | TF-021 AC2, TF-022, TF-025, TF-026, TF-028, TF-029 | done (main, 8f19e4b) |
| T6 | Audit integrity chain, time source, audit search, export and retention, legal hold, operational logs apart | TF-061, TF-064 to TF-066, TF-069 | done (main, 6a77a80) |
| T7 | Field-level change history, electronic signatures, signature manifestation, audit-trail review | TF-062, TF-063, TF-219 | done (main, e0b79f9) |
| T8 | Evidence exports, second approval for sensitive changes, validation pack per release | TF-067, TF-068, TF-218 | done (main, b8dc368) |
| T9a | Hardening the web surface: security headers and a policy without inline code with its report endpoint, origins per tenant, a declared limit for every request field enforced by the build, rich text cleaned by an allow-list, file names normalised, SQL from strings failing the build, rate limits with Retry-After and a self-hosted proof-of-work check on the login (the "Use your company login" answer included, T4's review); the shell's server answering 404 for the API's paths (M4's later item); the control plane's own database roles and its own sealed audit trail, anchored outside its database and verified by a read-only role (T6's review); ADR-0053 | TF-070, TF-071, TF-072 | done (main, 1b5b3b0) |
| T9b | Hardening the transport and the keys: key management and rotation (token signing keys with key identifiers, unit tokens, database passwords through the update job, the factor key's second version, the sign-in check's signing key rotating with the other keys); separate secrets and store tokens for the control plane's API and the fleet, so the API never holds the fleet's administrative database connection (both T9a's review); encryption in transit inside the platform where the laptop can show it, and at rest as the Azure deployment's requirement, with encryption of chosen fields | TF-073, TF-074, TF-075 | done (main, f58f047) |
| T10a | How code reaches main: pull requests with every check green and main protected, the second developer's review as a dated exception, dependency, licence, static and container scans through one gate with expiring exceptions, a CycloneDX bill of materials per image in the release and its validation pack, threat models checked by the tests, the findings register and the monthly report, coverage floors, the nightly release and the minutes it saves | TF-076, TF-077 | done (main, through pull request #16) |
| T10b | Security monitoring and incident response: alerts with thresholds on lockouts, denials, impersonation, break-glass, mass exports and configuration changes, a misconfigured rule reported as failing, the incident runbook with severities, roles, communication, the 72-hour notice and the post-incident review; the lost spans' counter | TF-079, TF-130 AC4 | done (main, through pull request #18) |
| S3-close | Exceptions with their homes, a test of a provider that cannot be reached (TF-014 AC10), the documentation true for S3, GAMP 5 and ALCOA+ in the PRD's section 5a, these closing notes, and the skeletons of slice V and S4 | | done (main, through its pull request) |

### Later items folded into the tasks

STATUS's later items that belonged to S3, each in the task it fitted (T1 decided the fold; the design session moved an item when it sharpened a task):

- **T1**: the shell's page tests that timed out beside the stack straight after `make test` (measured and settled, ADR-0044); the tests tagged with every PRD's identifiers (TF-218).
- **T2**: a password reset an operator can use when a tenant's administrator password is lost, which also lets the smoke and the browser tests set a password again; an API that disables a person, so that each browser test run and each smoke run stop leaving an inactive unit with its people behind; the last-administrator guard counting a disabled or locked administrator, since T2 brings the locked state.
- **T5**: "Sign out everywhere" in the sessions console; a response header with the session's idle end, so the shell's estimate is exact when a tenant shortens the limits.
- **T8**, before it started: the release's "Traceability matrix" job showed no conclusion in run 39620828352; checked in T8: it concluded and kept its artifact in each of the twelve release runs before T8, and its downloads are now tried three times (ADR-0052).
- **T9**: rate limits that bound a client calling a refused endpoint, each refusal writing `entitlements.access_denied` to the audit trail; the shell's web server answering 404, not its page, for `/api/` and `/health/` paths (T3's review); the factor key's rotation (ADR-0047); rate limits that cover the sign-in's "Use your company login" answer, which tells an identifier that signs in only through a provider (T4's review, ADR-0048).
- **T10a**: Dependabot's cooldown and the two packages it is told to leave, under the dependency policy with its fix times (done: `docs/security/README.md`); the images' SBOM (done, ADR-0055). The chiseled images' Kerberos probe message is explained by their bills of materials, which hold no Kerberos library for Npgsql's GSS probe.
- **T10b**: a system role's new permissions from the seed shown in the conflicts report and raised as a privilege grant, which TF-079's alerts cover; the counter of lost spans (TF-130 AC4), which the S1 plan listed as built and was not (T1's review, 2026-10-21).
- **S3-close**: a test of what a person sees when their company login answers 503 (T10b's review).

## 5. How we build

As in S2: each task was one Claude Code session from its section in `docs/prompts/S3.md` (ADR-0023), on its own branch from main, never stacked. The result was reviewed in the design session from the diff and the test output; fixes went back as the next prompt. A task was done when its "done when" held on the laptop, its tests and documentation were in, and its ADRs were written. From T10a on, the review is of a pull request whose every check is green, and the merge is the pull request's.

## 6. Decisions recorded

Thirteen ADRs, each written in the task that decided it and accepted at its review: 0044 (naming what a test proves and the matrix, T1), 0045 (process maps as BPMN files, SUP-gaps, beside S3), 0046 (passwords, lockout and recovery, T2), 0047 (second factors and step-up, T3), 0048 (single sign-on with OpenID Connect, T4), 0049 (sessions, revocation, impersonation, break-glass and terms, T5), 0050 (the audit chain, retention, legal holds, the time source and logs apart, T6), 0051 (change history and electronic signatures, T7), 0052 (evidence, four-eyes and the validation pack, T8), 0053 (hardening the web surface, T9a), 0054 (keys, encryption and secrets, T9b), 0055 (how code reaches main, the scans and the nightly release, T10a) and 0056 (security monitoring, incidents and the lost spans, T10b). Most were accepted with changes the review asked for, each change in the task's row of `docs/STATUS.md`.

## 7. Closing notes (2026-10-29)

S3 closed with every task's "done when" met on the laptop and the proofs of section 2 in place. On main after S3-close's merge: `make verify`, `make smoke TENANT=echo`, `make e2e TENANT=echo` and the release run, and the tag `s3-done`.

### What differed from the plan, and why

- **Two tasks split.** T9 became T9a (the web surface) and T9b (the transport, the keys and the secrets), and T10 became T10a (how code reaches main) and T10b (monitoring and incidents). Each half was a session's worth on its own, and each split let the review of the first half feed the second: T9a's review put the separate secrets of the control plane's API and the fleet into T9b, and T10a's pull requests carried T10b.
- **The first release with its validation pack.** From T8 every release stamps its version into the images and publishes a validation pack: the matrix with the tests' results, the installation record of each unit's update, and since T10a a bill of materials per image. A regulated tenant downloads the pack of the version it runs (TF-218).
- **Pull requests from T10a on.** The repository became public on 2026-10-28 at the owner's word, since GitHub protects a private repository's branch only on a paid plan, after gitleaks found nothing in history and tree. Main refuses a push, a force push and a merge with a check that is not green, administrators included; T10a's pull request #16 was the first change to reach main that way, T10b's #18 the second, and S3-close's the third. The second developer's review is PE-1 until one joins.
- **Defects the laptop and the pipeline found**, each fixed in its task with a test where one could hold it:
  - a session touched by two requests at once raced its own update and failed a browser test (T6, 98627cb);
  - main's release after T9a reached its 45 minutes: on CI's tenant, which starts on Standard, the smoke polled the role export, spent the exports limit, and waited the hour its Retry-After asked; the smoke now polls a dry-run import and refuses a wait over two minutes (T9b, 05c36e8);
  - the policy without inline code broke two libraries: Mantine's scroll lock style now takes the page's nonce, and Zod's eval probe is turned off before any schema is made (T9a, ca422e6, 954772d and a4880cf);
  - the enrolment page flashed under load until it waited for the new `/me` (T3, 0be2193);
  - the passkey counter after a signature, the installation record that missed `kernel_data`, an approved setting applied up to 30 seconds late, and hosts that used two modules' private classes (T7, T8);
  - main's CI after T9b failed a rotation test that waited a fixed second; it now waits on the requests it expects (01bdba1);
  - merged by file name, coverage counted 441 files twice and read 56.0% instead of 90.35% (T10a);
  - the audit page's date filter sent `ge`, which the API refuses, and its test stubbed the same mistake (T10b);
  - the shared test provider answered 503 under `make test`'s load, which T10b's verify runs met twice; its test host now waits out that one answer, and S3-close's test proves what a person sees when a real provider does so;
  - WSL's clock drifted 1.02 seconds and the clock check raised its alert, as designed (T6).
- **Decisions taken in review**, beyond what the sections asked, are in each task's row of `docs/STATUS.md`: among them the one-time place in the store for an operator's link (T2), an enrolment's 15 minutes and the policy applying at the next request (T3), step-up through a provider as a true fresh sign-in (T4), the sessions page's layout and `make accept-documents` (T5), accepted restore points and segments (T6), field history on the configuration records (T7), "My pending changes" for everyone (T8), and the passwords of Identity's tests picked at run time (T9b).

### What the method learned

- A prompt sharpened just before its task, against the code as it then stands, needed fewer corrections than an outline written a slice ahead; the split tasks show it.
- A test that names what it proves made gaps visible: the matrix found TF-130 AC4 listed as built and not built (T1), TF-020 AC3 proven by T9b's test without its name (S3-close), and S3's exceptions one by one.
- The laptop finds what the tests do not: limits that meet each other (the exports limit and the smoke), libraries under a strict policy, a clock that drifts. Every task ran the stack before its review, and that is where most defects of the list above appeared.
- A wait on a fixed time fails on a slower machine; every wait now waits on its condition with a bound.
- Under load the machine, not the code, fails first: Vitest's workers follow available memory, the .NET suite runs four projects at a time, and WSL's memory is the runbook's first symptom.

### Open items carried into V and S4

From STATUS, each with its home; the rest of STATUS's later items name S5, S6, S7, S8 or the first Azure deployment:

- **Into V.** TF-241 (content visibility) and TF-244 (suites per tenant); the fleet image's five Go findings, excepted until 2026-11-28, before V's first task; dropping the old column of factor secrets once every unit carries T9b.
- **Into S4.** The emails S3's exceptions wait for: the lockout (TF-012 AC2), a person's own recovery link (TF-017), the invitation and its resend (TF-018 AC3), the security events (TF-019 AC1 to AC3), the person impersonated (TF-025), a pending change's notices (TF-068), the MFA fallback by email or SMS (TF-013) and the factor emails (ADR-0047); the PDF of the evidence pack (TF-067) and of the signature block (TF-219) with the document templates; the first rich-text fields; the files and their virus scan (TF-078); the jobs a request starts (TF-130 AC3) and a tenant's jobs in its worker (TF-003 AC2).
