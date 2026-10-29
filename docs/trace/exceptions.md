# Exceptions to the traceability matrix

The criteria of a done slice's requirements that no test proves, each with its reason and the date the design session approved it (ADR-0044). `make trace` fails on any other criterion of a done slice without a test, on a row here that a test now proves (remove the row in the same change as the test), and on a row outside a done slice. A row names one criterion, `TF-001 AC3`, or a whole requirement when none of its criteria has a test.

The first list was written by S3 T1 from the backfill of S1's and S2's tests and approved at T1's review on 2026-10-21, which turned the six gaps it found into tests rather than exceptions. S3-close added S3's rows and moved TF-020 AC1's home; the design session approved every row and the parts listed beside them at S3-close's review, 2026-10-29. What each slice built and where each requirement continues is in the coverage table of its plan, section 2 of `docs/slices/S1-walking-skeleton.md` and of `docs/slices/S2-access-control-organisations-and-design.md`, section 3 of `docs/slices/S3-trust-and-compliance.md`.

## Not built yet

| Criterion | Reason | Approved |
|---|---|---|
| TF-001 AC3 | A unit does not refuse its users while its tenant is Suspended: S1 built the registry's statuses, and the lifecycle operations are TF-004's, in S6. | 2026-10-21 |
| TF-003 AC2 | No job runs in a tenant's worker yet; the jobs engine is S4. The worker's heartbeat span carries the tenant (`HeartbeatTests`, ADR-0011). | 2026-10-21 |
| TF-020 AC1 | No refresh token exists yet: people sign in to a server-side session, through a password or a company login (ADR-0048), and API clients take 15-minute tokens by the client credentials grant (ADR-0015). Refresh tokens, their rotation and the family's revocation come with the partner API surfaces and their SDKs in S7; the notice to the person with S4's notifications. | 2026-10-29 |
| TF-018 AC3 | An invitation is not resent: its link is given once, to the administrator who made the person or to the operator in the provisioning report (ADR-0046), and a person still Invited gets a new one only by reactivation or the seed. The invitation by email with its resend, the earlier link refused, comes with S4's notifications. | 2026-10-29 |
| TF-019 AC1 | The security events are recorded, audited and shown in My profile's Security tab (S3 T2, T5); the email of a new device's sign-in is S4's notifications. | 2026-10-29 |
| TF-019 AC2 | As AC1: the email's link waits for the email. The page it opens exists: My profile's Sessions tab lists the person's live sessions with their end actions (TF-022 AC4). | 2026-10-29 |
| TF-019 AC3 | As AC1: with no email there is no send to fail. Every event the email would carry is audited already. | 2026-10-29 |
| TF-023 AC2 | A service account has no address allow-list yet; it comes with the partner API surfaces in S7, beside API keys (TF-024), which need the same check. | 2026-10-29 |
| TF-023 AC3 | A service account's secret is replaced, not rotated with an overlap; the overlap comes with S7, as AC2. | 2026-10-29 |
| TF-037 AC1 | No workflow exists yet; a workflow assigns the delegate in S5 (ADR-0037). | 2026-10-21 |
| TF-038 AC3 | A review seven days late is escalated, recorded and audited (`ReviewTests`, ADR-0037); notifying the reviewer's manager waits for S4's notifications. | 2026-10-21 |
| TF-051 AC1 | The email kill switch is declared and wired when S4 builds email; only the imports switch is wired today (ADR-0035). | 2026-10-21 |
| TF-051 AC2 | As AC1: retrying blocked emails comes with S4's email. | 2026-10-21 |
| TF-056 AC1 | One flag provider exists, the in-house one behind OpenFeature (ADR-0035); a second provider arrives when one is needed. | 2026-10-21 |
| TF-056 AC3 | As AC1: with one provider there is no primary to choose. | 2026-10-21 |
| TF-130 AC3 | No job is started by a request yet (S4). | 2026-10-21 |
| TF-160 AC2 | Uploads, downloads and their permission check are S4; S1 built the storage interface and its readiness. | 2026-10-21 |
| TF-160 AC3 | File versions are S4. | 2026-10-21 |
| TF-171 AC2 | Saved views of the grid are S5. | 2026-10-21 |
| TF-173 AC1 | A tenant cannot set its colours yet: its mark and pattern come from its hostname (ADR-0026); branding settings come with S4's emails. The generator keeps every accent within the contrast pairs (ADR-0025). | 2026-10-21 |
| TF-173 AC2 | As AC1: no logo upload before S4's files. | 2026-10-21 |
| TF-173 AC3 | As AC1: with no branding setting there is only the default, which no test states as such. | 2026-10-21 |
| TF-180 AC3 | The operator console has no screens yet; the fleet view is S6. | 2026-10-21 |
| TF-190 AC1 | The scaffolding tool was planned after S1 and is not built; proposed for the first business slice, whose module would be its template. | 2026-10-21 |
| TF-221 AC1 | Numbering sequences (TF-166) are not built yet; their organisation scope comes with them. | 2026-10-21 |

## Parts that wait, inside criteria a test proves

The trace counts a criterion, so these parts of S3's requirements need no row: each criterion has its test, and the part named here is S4's notifications or S4's document templates. They were approved with S3's rows on 2026-10-29, and S4's plan names each.

- TF-012 AC2: the lockout is refused and recorded (T2's tests); its email is S4's.
- TF-013: the requirement's fallback by one-time codes by email or SMS is S4's (ADR-0047); the emails for a new factor, a removal and a reset join TF-019's.
- TF-017: a person's own recovery link to their verified email is S4's; the link an administrator or the operator issues is built (T2).
- TF-018: the invitation by email is S4's; the link is given once to whoever asked (T2).
- TF-025: the email to the person impersonated is S4's; they see it in their security activity (AC6, T5).
- TF-068: a pending change's notices to its approvers and to its requester when it is decided or expires are S4's; the console and My profile show them (T8).
- TF-067: the evidence pack's PDF is S4's document templates; each document is a readable page and a CSV (T8).

## Proven by hand or by the pipeline's design

Each reason ends with where the proof is recorded, by document and date.

| Criterion | Reason | Approved |
|---|---|---|
| TF-003 AC3 | Traefik answers a hostname no unit serves with its own 404 and routes nothing (ADR-0019). Proof: `docs/guides/local-stack.md`, section 5, "A hostname no unit serves", checked on 2026-10-21. | 2026-10-21 |
| TF-130 AC1 | One trace with API and database spans is S1's demo, and the smoke's `trace` step finds the request's trace in Tempo on every run; no test counts the spans by kind. Proof: `docs/slices/S1-walking-skeleton.md`, section 1 item 4 and the closing notes of section 7, 2026-10-18; the smoke's report on every run since, last on 2026-10-21 (STATUS, S3 T1). | 2026-10-21 |
| TF-172 AC4 | The scan job fails CI on any violation, its own job with no retries; that a pull request with a violation fails is the job's design, not a test. Proof: ADR-0025, decision 10, 2026-10-18; `docs/slices/S2-access-control-organisations-and-design.md`, closing notes of section 7, 2026-10-20 (the scan 60 of 60 in CI and on the laptop). | 2026-10-21 |
| TF-190 AC3 | A fresh checkout reaches a provisioned tenant within the hour by the README, with the slim profile beside the full one. Proof: `docs/slices/S1-walking-skeleton.md`, T19's done-when in section 4 and the closing notes of section 7, 2026-10-18; `docs/STATUS.md`, the L1 and P1 rows, the laptop reset and acme, bravo and echo provisioned again, 2026-10-19. | 2026-10-21 |
| TF-209 AC1 | A review criterion: a decision without its ADR is not merged. Proof: `docs/STATUS.md`, the S2 task table, each task's row naming the ADR its review accepted, 2026-10-18 to 2026-10-20; `docs/slices/S2-access-control-organisations-and-design.md`, section 6, the eighteen ADRs each written in the task that decided it, 2026-10-20. | 2026-10-21 |
| TF-209 AC2 | A review criterion: every task is built by a session that starts with the guide and nothing else (ADR-0023). Proof: `docs/STATUS.md`, the A4, A5 and A6 rows, three kernel modules with their entities, migrations and endpoints built that way, 2026-10-20. | 2026-10-21 |
