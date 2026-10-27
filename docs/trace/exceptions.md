# Exceptions to the traceability matrix

The criteria of a done slice's requirements that no test proves, each with its reason and the date the design session approved it (ADR-0044). `make trace` fails on any other criterion of a done slice without a test, on a row here that a test now proves (remove the row in the same change as the test), and on a row outside a done slice. A row names one criterion, `TF-001 AC3`, or a whole requirement when none of its criteria has a test.

The first list was written by S3 T1 from the backfill of S1's and S2's tests and approved at T1's review on 2026-10-21, which turned the six gaps it found into tests rather than exceptions. What each slice built and where each requirement continues is in the coverage table of its plan, section 2 of `docs/slices/S1-walking-skeleton.md` and of `docs/slices/S2-access-control-organisations-and-design.md`.

## Not built yet

| Criterion | Reason | Approved |
|---|---|---|
| TF-001 AC3 | A unit does not refuse its users while its tenant is Suspended: S1 built the registry's statuses, and the lifecycle operations are TF-004's, in S6. | 2026-10-21 |
| TF-003 AC2 | No job runs in a tenant's worker yet; the jobs engine is S4. The worker's heartbeat span carries the tenant (`HeartbeatTests`, ADR-0011). | 2026-10-21 |
| TF-020 AC1 | No refresh token exists yet: S1 issues access tokens by the client credentials grant only (ADR-0015); the authorization code flow with refresh rotation comes with S3 T4. | 2026-10-21 |
| TF-020 AC3 | The signing key is read from the store and not rotated yet; key rotation is S3 T9 (TF-075). | 2026-10-21 |
| TF-037 AC1 | No workflow exists yet; a workflow assigns the delegate in S5 (ADR-0037). | 2026-10-21 |
| TF-038 AC3 | A review seven days late is escalated, recorded and audited (`ReviewTests`, ADR-0037); notifying the reviewer's manager waits for S4's notifications. | 2026-10-21 |
| TF-051 AC1 | The email kill switch is declared and wired when S4 builds email; only the imports switch is wired today (ADR-0035). | 2026-10-21 |
| TF-051 AC2 | As AC1: retrying blocked emails comes with S4's email. | 2026-10-21 |
| TF-056 AC1 | One flag provider exists, the in-house one behind OpenFeature (ADR-0035); a second provider arrives when one is needed. | 2026-10-21 |
| TF-056 AC3 | As AC1: with one provider there is no primary to choose. | 2026-10-21 |
| TF-130 AC3 | No job is started by a request yet (S4). | 2026-10-21 |
| TF-130 AC4 | Requests complete while the collector is down (S1's demo), but the counter of lost spans was never built, although the S1 plan said so until T1's review corrected it; it is S3 T10's, with the security monitoring. | 2026-10-21 |
| TF-160 AC2 | Uploads, downloads and their permission check are S4; S1 built the storage interface and its readiness. | 2026-10-21 |
| TF-160 AC3 | File versions are S4. | 2026-10-21 |
| TF-171 AC2 | Saved views of the grid are S5. | 2026-10-21 |
| TF-173 AC1 | A tenant cannot set its colours yet: its mark and pattern come from its hostname (ADR-0026); branding settings come with S4's emails. The generator keeps every accent within the contrast pairs (ADR-0025). | 2026-10-21 |
| TF-173 AC2 | As AC1: no logo upload before S4's files. | 2026-10-21 |
| TF-173 AC3 | As AC1: with no branding setting there is only the default, which no test states as such. | 2026-10-21 |
| TF-180 AC3 | The operator console has no screens yet; the fleet view is S6. | 2026-10-21 |
| TF-190 AC1 | The scaffolding tool was planned after S1 and is not built; proposed for the first business slice, whose module would be its template. | 2026-10-21 |
| TF-209 AC3 | No alert exists yet with a runbook of its own; alerting is S6 (TF-134) and S3 T10 writes the incident runbook. | 2026-10-21 |
| TF-209 AC4 | As AC3. | 2026-10-21 |
| TF-221 AC1 | Numbering sequences (TF-166) are not built yet; their organisation scope comes with them. | 2026-10-21 |

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
