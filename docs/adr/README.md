# Architecture decision records

Names in ADRs before 0029 are the names of their time; ADR-0029 maps them to today's.

Every `[Decision]` in the PRD, and every decision made while building, gets one record here (TF-209). A pull request that introduces a decision without its ADR is not merged.

Copy `0000-template.md` to the next free number. Keep the record short. Never edit an accepted decision; write a new one that supersedes it.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-with-contracts-projects.md) | Modular monolith with one Contracts project per module | Accepted; names and layers extended by 0029 |
| [0002](0002-isolated-deployment-unit-per-tenant.md) | One isolated deployment unit per tenant on shared infrastructure | Accepted |
| [0003](0003-open-source-first-and-decided-versions.md) | Open source first, every dependency listed once, decided platform versions | Accepted |
| [0004](0004-build-and-test-conventions.md) | Build and test conventions | Accepted |
| [0005](0005-laptop-platform-stack.md) | The laptop platform stack on Compose | Accepted; OpenBao's mode superseded by 0028 |
| [0006](0006-object-storage-stand-in.md) | Azurite replaces MinIO as the laptop object storage | Accepted |
| [0007](0007-core-primitives.md) | Core primitives: results and errors, tenant identifier, ids, time, correlation | Accepted |
| [0008](0008-data-conventions.md) | Data conventions | Accepted; schema prefixes and a one-time baseline by 0029 |
| [0009](0009-api-conventions.md) | API conventions | Accepted |
| [0010](0010-telemetry-and-security-baseline.md) | Telemetry and security baseline | Accepted |
| [0011](0011-hosts-and-unit-commands.md) | Hosts, tenant binding and unit commands | Accepted |
| [0012](0012-authorization-thin.md) | Permissions, roles and the migration workflow | Accepted |
| [0013](0013-audit-trail-and-unit-of-work.md) | Audit trail, unit of work and the app role | Accepted |
| [0014](0014-identity-sessions-and-passwords.md) | Principals, passwords and sessions | Accepted |
| [0015](0015-access-tokens.md) | Access tokens for API clients | Accepted |
| [0016](0016-secrets-and-object-storage.md) | Secrets at start and object storage | Accepted |
| [0017](0017-control-plane-tenant-registry.md) | The tenant registry and the control plane API | Accepted |
| [0018](0018-provisioning-steps.md) | Provisioning as resumable steps | Accepted |
| [0019](0019-compose-unit-runtime.md) | Compose as the laptop unit runtime | Accepted |
| [0020](0020-shell.md) | The shell | Accepted; the update's steps superseded by 0028 |
| [0021](0021-data-grid.md) | DataGrid over AG Grid Community | Accepted |
| [0022](0022-pipeline.md) | The pipeline | Accepted |
| [0023](0023-build-method.md) | The build method from S2: instruction files for Claude Code | Accepted |
| [0024](0024-design-language.md) | The design language | Accepted |
| [0025](0025-design-foundation.md) | The design foundation | Accepted |
| [0026](0026-illustrations-and-patterns.md) | Illustrations and patterns: the generator, the hand and the review rule | Accepted |
| [0027](0027-templates-panels-and-the-form.md) | Templates, panels and the form | Accepted |
| [0028](0028-laptop-store-persists-and-updates-heal.md) | The laptop store persists, and an update heals a unit | Accepted |
| [0029](0029-three-layers-kernel-business-products.md) | Three layers: kernel, business, products | Accepted |
| [0030](0030-organisation-tree-and-scope-evaluation.md) | The organisation tree as a materialised path, and how a scope is evaluated | Accepted |
| [0031](0031-nobody-grants-more-than-they-hold.md) | Nobody grants more than they hold | Accepted |
| [0032](0032-record-policies.md) | Record policies, and a forgotten policy does not compile | Accepted |
| [0033](0033-home-the-header-and-the-session-warning.md) | Home, the header, and the session warning across tabs | Accepted |
| [0034](0034-entitlements-the-kernel-outside-plans-and-the-store-as-the-channel.md) | Entitlements: the kernel outside plans, one catalogue, and the store as the channel | Accepted |
| [0035](0035-feature-flags-declared-in-code-ruled-in-the-control-plane.md) | Feature flags: declared in code, ruled in the control plane, last known before defaults | Accepted |
| [0036](0036-settings-typed-definitions-resolved-in-the-unit.md) | Settings: typed definitions as the accessor, resolved in the unit, preferences that follow the person | Accepted |
| [0037](0037-delegation-acting-for-and-access-reviews.md) | Delegation of duties, acting for the delegator, and access reviews | Accepted |
| [0038](0038-sharing-read-audit-working-unit-and-unit-lifecycle.md) | Record sharing, the cross-unit read audit, the working unit, and the unit lifecycle | Accepted |
| [0039](0039-explaining-access-and-segregation-of-duties.md) | Explaining access, and segregation of duties by overlapping scope | Accepted |
| [0040](0040-prettier-formats-the-shell.md) | Prettier formats the shell | Accepted |
| [0041](0041-one-requirements-document-per-layer-and-product.md) | One requirements document per layer and product | Accepted |
| [0042](0042-dependencies-down-the-layers-and-along-the-process.md) | Dependencies go down the layers, and along the process between products | Accepted |
| [0043](0043-ironsaas-foundation-and-suites.md) | IronSaaS Foundation and suites: the Suite layer, deployment by purchase, one repository | Accepted |
| [0044](0044-naming-what-a-test-proves-and-the-traceability-matrix.md) | Naming what a test proves, the traceability matrix, and a check sequence that holds under load | Accepted |
| [0045](0045-process-maps-as-bpmn-files-checked-against-the-schema.md) | Process maps as BPMN files, checked against the BPMN 2.0 schema | Accepted |
| [0046](0046-passwords-lockout-and-recovery.md) | Passwords: the policy, lockout without revealing accounts, and three ways to recover | Accepted |
| [0047](0047-second-factors-and-step-up.md) | Second factors, the MFA policy, recovery of a lost device, and step-up re-authentication | Accepted |
| [0048](0048-single-sign-on-with-openid-connect.md) | Single sign-on with OpenID Connect: providers, the code flow, who it signs in, and a test provider | Accepted |
| [0049](0049-sessions-revocation-impersonation-break-glass-and-terms.md) | Sessions, revocation, impersonation, break-glass, emergency accounts and terms | Accepted |
| [0050](0050-audit-chain-time-retention-and-legal-holds.md) | The audit chain, its verification, archive and retention, legal holds, the time source and logs apart | Accepted |
| [0051](0051-change-history-and-electronic-signatures.md) | Change history in the audit chain, electronic signatures bound to their version, and identifiers never reused | Accepted |
| [0052](0052-evidence-four-eyes-and-the-validation-pack.md) | Evidence packs, four-eyes on sensitive configuration, release versions and the validation pack | Accepted |
| [0053](0053-hardening-the-web-surface.md) | Hardening the web surface: headers without inline code, origins, declared limits, rich text, SQL, rate limits, the login's challenge and the control plane's own role and chain | Accepted |
| [0054](0054-keys-encryption-and-secrets.md) | Keys, encryption and secrets: a key service with keys per tenant, encrypted and searchable fields, rotation without downtime, passwords read again, separate control plane secrets, and TLS 1.2 and up | Accepted |
| [0055](0055-how-code-reaches-main-scans-and-the-nightly-release.md) | How code reaches main: pull requests with every check green, the review exception, one security gate with expiring exceptions, bills of materials, threat models, coverage floors and the nightly release | Accepted |
| [0056](0056-security-monitoring-incidents-and-lost-spans.md) | Security monitoring over the audit trail, the operator's alerting, incidents with the 72-hour notice, and the lost spans | Accepted |
| [0057](0057-content-visibility-and-visibility-tags.md) | Content visibility: visibility tags in the record policy's query, stored on the record, measured first | Accepted |
| [0058](0058-the-eprocurement-suites-modules-waves-and-licensing.md) | The eProcurement Suite's modules, their waves and their licensing, and Requests and Intake's buying model as configuration | Accepted |
| [0059](0059-suites-per-tenant-plans-per-suite-and-what-a-unit-loads.md) | Suites per tenant: the eProcurement Suite's skeleton, a plan per suite, and what a unit loads | Accepted |
| [0060](0060-domain-events-the-outbox-subscriptions-and-replay.md) | Domain events: declared in code, an outbox in the change's transaction, subscriptions with an inbox, replay and a broker later | Accepted |
| [0061](0061-jobs-declared-in-code-coordinated-in-postgresql.md) | Jobs: declared in code, coordinated in the unit's PostgreSQL, retried into dead letters, the worker's loops among them | Accepted |

## Reading order

For a newcomer: 0001, 0029 and 0043 (the shape of the code, its three layers, and the suites that are its third), 0002 (the shape of a deployment), 0007 to 0010 (the conventions every line of code follows), 0012 to 0015, 0046 to 0049, 0030 to 0032, 0057 and 0037 to 0039 (how people sign in, with a second factor or a company login, and recover, how their sessions end and who may sign in as them, who may do what, where, who may grant it, which records each may see and share, how visibility tags narrow them, who acts for whom and checks it, which unit a person works for, why a person may or may not do something, and which rights one person must not hold together), 0050 (how the audit trail is sealed into a chain, verified from outside the unit, archived, kept and held, whose clock every time is, and why logs are not the record), 0051 (how every change is in that chain, how a signature binds to the version it signs, and why a login identifier is never given twice), 0052 (what an auditor gets for a period, how sensitive configuration waits for a second administrator, and what every release proves and every tenant can download), 0053 (what the web surface refuses and why: inline code, other sites, fields without limits, scripts in rich text, SQL from strings, floods and automated sign-ins, and how the control plane keeps its own trail), 0054 (where the keys are and who never holds them, how a chosen field is encrypted and still found by its exact value, how keys and passwords rotate without a restart or a failed request, and why the control plane's API cannot read the fleet's secrets), 0055 (how a change reaches main, what the pipeline scans and refuses, how an exception runs out, and why the release runs at night), 0056 (how the audit trail is watched for attacks, how an alert reaches the operator, how an incident runs to its notice and its review, and why a lost span never holds a request), 0034 and 0035 (what a tenant may use, which flags and kill switches apply, and how its unit learns both), 0036 (the tenant's own settings), 0016 to 0019 and 0028 (how a tenant comes to exist, and how it heals), 0020 to 0022 (the shell and the pipeline), 0023 to 0027, 0033 and 0040 (how S2 onwards is built, how it looks, the shell's home and frame, and how its code is formatted), 0041, 0042 and 0044 (where the requirements live, how they depend on each other, and how every test names the criteria or the decision it proves), 0045 (how a process map is drawn and checked), 0058 (which modules the eProcurement Suite has, how each is sold, and in which wave it is built), 0059 (how a tenant gets the suites it buys, how a plan per suite combines with the Foundation's, and what a unit loads at start), 0060 (how a module tells others of a change in its own transaction, how each subscriber handles it once and in order, what happens when one fails, and how a broker could take over), 0061 (how background work is declared, run once by any number of workers, retried into dead letters and alerted, ordered by class, kept fair between tenants, and steered from the console). Every ADR of S3 (0044 to 0056), of V (0057 to 0059) and of S4 so far (0060, 0061) is in this sentence. The slice plans `docs/slices/S1-walking-skeleton.md`, `docs/slices/S2-access-control-organisations-and-design.md`, `docs/slices/S3-trust-and-compliance.md` and `docs/slices/V-visibility-and-suites.md` tell the story in order, with what differed from each plan and why.
