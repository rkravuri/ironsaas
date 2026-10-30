# Slice V: content visibility and suites

The slice before the first business slice (the technical PRD's section 7.1): visibility tags on records and people, the filter inside the query after roles and units, the explanation and the reviews; a tenant's suites installed by purchase, with a plan per suite beside the Foundation plan. Every business record type needs visibility tags from its first day, and the eProcurement Suite's first module is installed only for the tenants that bought the suite, so both come first (section 7.2). This file is the skeleton S3-close left: the requirements, what earlier slices built towards them, and an empty task table. Its tasks are written in the design session, as S3's were.

## 1. What V proves

To be written in the design session.

## 2. Requirement coverage

| ID | Requirement | Priority | Built before V | Notes for the design session |
|---|---|---|---|---|
| TF-241 | Content visibility and visibility tags | Must | Record policies applied as list filters in the query, which only narrow (S2 A3, ADR-0032); the access explanation from the gates' own decisions (S2 A9, ADR-0039); access reviews (S2 A7, ADR-0037); field history and the audit of every tracked change (S3 T7, ADR-0051) | No kernel module declares a business record yet, so the tags meet their first records in a fixture module, as the module checks did in S2; each product says how its transactions take their tags from their master (rule 6) |
| TF-244 | Suites per tenant | Must | The Suite layer in the build, the catalogue grouped by suite and the shell's `modules/suites/` (L2, ADR-0043); plans and entitlements in the control plane with the unit's refresh (S2 A4, ADR-0034); the update job that heals a unit (S2 P1, ADR-0028); the installation record of each update (S3 T8, ADR-0052) | Until it is built a unit loads every module `Iron.Host.Api.UnitModules` lists, and no suite module exists; a plan naming another group's module cannot yet be refused (AC7) |

## 3. Carried in from S3

From STATUS, the items whose home S3-close made V:

- The fleet image's Docker CLI and Compose plugin carry five Go findings excepted until 2026-11-28 (ADR-0055): take the first `docker:<version>-cli` built with Go 1.26.10 or later once it is two weeks old, and remove the exceptions, before V's first task. In V0: `docker:29.9.2-cli` fixes four, taken on 2026-11-04; the fifth, in the Compose plugin's x/net, waits for a Compose release, its exception renewed with a corrected reason.
- The old column of factor secrets (`factors.secret_ciphertext`) is dropped by a migration, every unit carrying T9b (ADR-0054). In V0, with the migration and the fleet refusing a unit not yet moved.

## 4. Task breakdown

The tasks of `docs/prompts/V.md`, one section each.

| Task | What it builds | Requirements | State |
|---|---|---|---|
| V0 | Chores: the fleet image's five findings before their exceptions run out on 2026-11-28; dropping the old factor-secret column | | done |
| V1 | Content visibility: visibility tags on people and records | TF-241 | done |
| V2 | Suites per tenant: a customer gets the suites it buys; the eProcurement Suite's skeleton, the Purchasing module with its About page, is the first suite module a unit installs (`docs/prd/suites/procurement/README.md`, ADR-0058) | TF-244 | outline |
| V-close | Documentation, exceptions and closing notes | | outline |

## 5. How we build

As in S3: each task is one Claude Code session from its section in `docs/prompts/V.md` (ADR-0023), on its own branch from main, never stacked, and reaches main through a pull request with every check green (ADR-0055). The design session writes each section just before its task starts. Every test names what it proves (ADR-0044), and V becomes a done slice in the matrix when every task of the table above is done.
