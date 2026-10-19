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

## Reading order

For a newcomer: 0001 and 0029 (the shape of the code and its three layers), 0002 (the shape of a deployment), 0007 to 0010 (the conventions every line of code follows), 0012 to 0015 and 0030 to 0032 (who may do what, where, who may grant it, and which records each may see), 0016 to 0019 and 0028 (how a tenant comes to exist, and how it heals), 0020 to 0022 (the shell and the pipeline), 0023 to 0027 and 0033 (how S2 onwards is built, how it looks, and the shell's home and frame). The slice plan `docs/slices/S1-walking-skeleton.md` tells the story in order.
