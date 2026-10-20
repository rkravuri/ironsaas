# Exceptions to the traceability matrix

The criteria of a done slice's requirements that no test proves, each with its reason and the date the design session approved it (ADR-0044). `make trace` fails on any other criterion of a done slice without a test, on a row here that a test now proves (remove the row in the same change as the test), and on a row outside a done slice. A row names one criterion, `TF-001 AC3`, or a whole requirement when none of its criteria has a test.

The first list was written by S3 T1 from the backfill of S1's and S2's tests (2026-10-21); its rows carry that date until the design session's review of T1 approves them or changes them. What each slice built and where each requirement continues is in the coverage table of its plan, section 2 of `docs/slices/S1-walking-skeleton.md` and of `docs/slices/S2-access-control-organisations-and-design.md`.

## Not built yet

| Criterion | Reason | Approved |
|---|---|---|
| TF-001 AC3 | A unit does not refuse its users while its tenant is Suspended: S1 built the registry's statuses, and the lifecycle operations are TF-004's, in S6. | 2026-10-21 |
| TF-003 AC2 | No job runs in a tenant's worker yet; the jobs engine is S4. The worker's heartbeat span carries the tenant (`HeartbeatTests`, ADR-0011). | 2026-10-21 |
| TF-010 AC3 | No API disables a principal yet; S3 T2 brings the account states and T5 the revocation of a person's sessions. | 2026-10-21 |
| TF-020 AC1 | No refresh token exists yet: S1 issues access tokens by the client credentials grant only (ADR-0015); the authorization code flow with refresh rotation comes with S3 T4. | 2026-10-21 |
| TF-020 AC3 | The signing key is read from the store and not rotated yet; key rotation is S3 T9 (TF-075). | 2026-10-21 |
| TF-021 AC2 | The concurrent session limit is S3 T5, with the sessions console. | 2026-10-21 |
| TF-037 AC1 | No workflow exists yet; a workflow assigns the delegate in S5 (ADR-0037). | 2026-10-21 |
| TF-038 AC3 | A review seven days late is escalated, recorded and audited (`ReviewTests`, ADR-0037); notifying the reviewer's manager waits for S4's notifications. | 2026-10-21 |
| TF-051 AC1 | The email kill switch is declared and wired when S4 builds email; only the imports switch is wired today (ADR-0035). | 2026-10-21 |
| TF-051 AC2 | As AC1: retrying blocked emails comes with S4's email. | 2026-10-21 |
| TF-056 AC1 | One flag provider exists, the in-house one behind OpenFeature (ADR-0035); a second provider arrives when one is needed. | 2026-10-21 |
| TF-056 AC3 | As AC1: with one provider there is no primary to choose. | 2026-10-21 |
| TF-060 AC3 | No job acts for the system yet (S4); the audit writer's source and actor fields are proven for requests (`AuditTableTests`). | 2026-10-21 |
| TF-070 AC3 | The content security policy is sent, but its report endpoint is S3 T9, which re-checks the secure defaults. | 2026-10-21 |
| TF-130 AC3 | No job is started by a request yet (S4). | 2026-10-21 |
| TF-130 AC4 | Requests complete while the collector is down (S1's demo), but the counter of lost spans the S1 plan names was not built; proposed for S3 T10 with the security monitoring, for the review to place. | 2026-10-21 |
| TF-160 AC2 | Uploads, downloads and their permission check are S4; S1 built the storage interface and its readiness. | 2026-10-21 |
| TF-160 AC3 | File versions are S4. | 2026-10-21 |
| TF-171 AC2 | Saved views of the grid are S5. | 2026-10-21 |
| TF-173 AC1 | A tenant cannot set its colours yet: its mark and pattern come from its hostname (ADR-0026); branding settings come with S4's emails. The generator keeps every accent within the contrast pairs (ADR-0025). | 2026-10-21 |
| TF-173 AC2 | As AC1: no logo upload before S4's files. | 2026-10-21 |
| TF-173 AC3 | As AC1: with no branding setting there is only the default, which no test states as such. | 2026-10-21 |
| TF-180 AC3 | The operator console has no screens yet; the fleet view is S6. | 2026-10-21 |
| TF-190 AC1 | The scaffolding tool was planned after S1 and is not built; proposed for the first business slice, whose module would be its template. | 2026-10-21 |
| TF-209 AC3 | No alert exists yet, so no runbook is referenced by one; alerting is S6 (TF-134) and S3 T10 writes the incident runbook. | 2026-10-21 |
| TF-209 AC4 | As AC3. | 2026-10-21 |
| TF-221 AC1 | Numbering sequences (TF-166) are not built yet; their organisation scope comes with them. | 2026-10-21 |

## Built, proven by hand or by the pipeline's design

| Criterion | Reason | Approved |
|---|---|---|
| TF-003 AC3 | Traefik answers a hostname no unit serves with its own 404 and routes nothing (ADR-0019); seen on the laptop, not tested. | 2026-10-21 |
| TF-130 AC1 | One trace with API and database spans is S1's demo, and the smoke's `trace` step finds the request's trace in Tempo on every run; no test counts the spans by kind. | 2026-10-21 |
| TF-172 AC4 | The scan job fails CI on any violation (ADR-0025); that a pull request with a violation fails is the job's design, not a test. | 2026-10-21 |
| TF-190 AC3 | Proven by hand at S1's close (T19: a fresh checkout to a provisioned tenant within the hour) and again at the laptop resets of L1 and P1. | 2026-10-21 |
| TF-209 AC1 | A review criterion: the definition of done and the pull request template ask for the ADR; no automated test. | 2026-10-21 |
| TF-209 AC2 | A review criterion: the developer guide is followed by every task's session; no automated test. | 2026-10-21 |

## Built, without a test of this criterion: gaps for the review

| Criterion | Reason | Approved |
|---|---|---|
| TF-030 AC2 | The role editor groups a module's permissions by module, not by resource (A2), so its test is untraced; the review decides whether the criterion reads "module" or the editor groups by resource. | 2026-10-21 |
| TF-031 AC3 | Health, login and invitation acceptance answer without a token, and the host test keeps that list fixed (TF-030 AC3); that they expose nothing tenant-specific has no test. | 2026-10-21 |
| TF-031 AC4 | Access is denied when the permission store is unavailable (`PermissionEvaluatorTests`, ADR-0012); the alert waits for S6's alerting. | 2026-10-21 |
| TF-080 AC3 | No foreign key crosses schemas (AC2's check), which is what keeps the other schemas valid when one is dropped; no test drops a module's schema. | 2026-10-21 |
| TF-100 AC3 | The OpenAPI document is generated from code; a test checks one operation's permission (`TokenEndpointTests`), none checks every operation with its schemas. | 2026-10-21 |
| TF-170 AC2 | The error view with its correlation id is proven (`ErrorBoundary.test.tsx`, TF-130 AC2); that an unhandled error reaches it and the shell stays usable has no test. | 2026-10-21 |
