# Slice V: content visibility and suites

The slice between S3 and S4 (the technical PRD's section 7.1): visibility tags on records and people, the filter inside the query after roles and units, the explanation and the reviews; a tenant's suites installed by purchase, with a plan per suite beside the Foundation plan. Every business record type needs visibility tags from its first day, and the eProcurement Suite's first module is installed only for the tenants that bought the suite, so both came before the first business slice (section 7.2). V was built with Claude Code from `docs/prompts/V.md` (ADR-0023), each task's section sharpened in the design session just before it started, from 2026-10-29 to 2026-10-31. S3-close left this file as a skeleton; V-close wrote what V proves, its coverage, its decisions and its closing notes.

## 1. What V proves

Each with where the proof is, on 2026-10-31:

- a person who holds visibility tags sees only the records that share one with them or carry none, in lists whose totals count what they see and by identifier, the filter inside the record policy's query after the scope rule and the module's policy, so it only narrows: the Data tests on the sample module (`VisibilityTagsTests`), the smoke's `visibility-tags` step on a unit's sample records, and the browser test `shell/e2e/visibility.spec.ts`;
- nobody puts on or takes off a tag they do not hold, unless they hold none; a retired tag still filters; tag changes on people and records are tracked and audited; the explanation of access names the tag check, and access reviews list each person's tags and can review tag holders: the Visibility, Data and Authorization tests;
- the storage of tags was measured before it was chosen, a column on each record's table against one kernel table (`tools/measurements/visibility-tags-storage.sh`, ADR-0057), and the model check refuses a business record whose table lacks it;
- a tenant gets the suites it buys: a unit migrates, seeds and loads a suite only where its tenant has it, and a tenant without it has no schema, endpoint or page of it: the host, Purchasing and Fleet tests, the smoke's `suites` step, and the laptop, where echo has the eProcurement Suite and bravo does not;
- adding a suite to an active tenant is an audited update job, and removing it switches its modules off and keeps its data, found again when it is added back: the Tenancy, Purchasing and Fleet tests, and bravo on the laptop, which gained the suite and lost it again;
- a plan belongs to the Foundation or to one suite and cannot name another group's module or feature, and each entitlement keeps its own plan's grace days: the Tenancy, Entitlements and Purchasing tests;
- every release proves both on a tenant registered with the suite and marked for sample records: the integration stage (`infra/compose/scripts/ci-integration.sh`) and its smoke;
- V is a done slice in the matrix: TF-241's eight criteria and TF-244's seven each have a test, and none needed an exception (`make trace`).

At the close: 1257 .NET test cases, 415 Vitest, 158 scans, 97 Python tests, the smoke's 26 steps and 44 browser tests against the stack; three ADRs (0057 and 0059, and 0058 beside V).

## 2. Requirement coverage

What V built for each requirement, and where it continues. V added no criterion: each of the fifteen was in the PRD before V started.

| ID | Requirement | What V built | Continues in |
|---|---|---|---|
| TF-241 | Content visibility and visibility tags | The Visibility module (`kernel_visibility`): tags that administrators define, rename, retire and reactivate; people's tags, given within the escalation rule; every business record's tags in a `text[]` column of its own table, chosen by measurement; the filter in the record policy's query for lists and reads by identifier; rule 5 for putting a tag on and taking it off; rule 6 as an extension point (`AddTagsFromMaster`); the explanation's tag check and reviews of tag holders; the console's Content visibility page, the user record's Visibility tab and the record template's tags; sample records in units marked for testing (V1, ADR-0057) | S5 (TF-243: approvals and tasks that respect the tags); S4 (notifications and digests that name a record, the part of TF-243 S4 meets first, S4's plan section 4); each product's PRD (the records that take their master's tags) |
| TF-244 | Suites per tenant | The suite declared in code with its modules and starter plans; a unit that loads the Foundation and the suites in `Suites__Installed`, which the fleet sets from the control plane's record; plans per group, refused when one names another group's module or feature, and the tenant's entitlements as their union, each entry with its own plan's grace; `make provision SUITES=`, `make suite-add` and `make suite-remove`, each audited with a reason and carried out by an update job; the installation record per composition; the eProcurement Suite's skeleton, Purchasing with its About page (V2, ADR-0059) | PR0 (Purchasing's masters; the suite's definition leaves the skeleton); S6 (a removed suite's data deleted at a tenant's offboarding, with TF-004) |

What V built on, each in its own plan's table: record policies applied as list filters in the query (S2 A3, ADR-0032), the explanation from the gates' own decisions (S2 A9, ADR-0039), access reviews (S2 A7, ADR-0037), field history (S3 T7, ADR-0051); the Suite layer in the build and the catalogue grouped by suite (L2, ADR-0043), plans and entitlements with the unit's refresh (S2 A4, ADR-0034, TF-040 to TF-046), the update job that heals a unit (S2 P1, ADR-0028), and the installation record of each update (S3 T8, ADR-0052, TF-218).

## 3. Carried in from S3

From STATUS, the items whose home S3-close made V:

- The fleet image's Docker CLI and Compose plugin carried five Go findings excepted until 2026-11-28 (ADR-0055). V0 found no release that fixed all five and stopped, as its section asked; the design session chose option A (section 7). Four stay excepted until the fleet moves to `docker:29.9.2-cli` on 2026-11-04 (M6), the fifth, in the Compose plugin's x/net, until a Compose release fixes it, renewed with a corrected reason until 2027-01-28.
- The old column of factor secrets (`factors.secret_ciphertext`): dropped in V0 by a migration that refuses while any value remains, with the fleet refusing a unit that still holds the retired key (ADR-0054).

## 4. Task breakdown

The tasks of `docs/prompts/V.md`, one section each.

| Task | What it builds | Requirements | State |
|---|---|---|---|
| V0 | Chores: the fleet image's five findings before their exceptions run out on 2026-11-28; dropping the old factor-secret column | | done (main, through pull request #20) |
| V1 | Content visibility: visibility tags on people and records | TF-241 | done (main, through pull request #28) |
| V2 | Suites per tenant: a customer gets the suites it buys; the eProcurement Suite's skeleton, the Purchasing module with its About page, is the first suite module a unit installs (`docs/prd/suites/procurement/README.md`, ADR-0058); how plans per suite combine and what a unit loads is ADR-0059 | TF-244 | done (main, through pull request #30) |
| V-close | Documentation, exceptions and closing notes | | done (main, through its pull request) |

Beside V, between V1 and V2: EPS-modules (pull request #29), the eProcurement Suite's module list and the Requests and Intake decisions, documentation only (ADR-0058).

### Later items folded into the tasks

- **V0**: the fleet image's five findings and the old factor column, from S3-close; at its review, the digests of the images still pinned by tag only, moved to M6 on 2026-11-04.
- **V2**: TF-244, which S3-close moved to V.
- **V-close**: the matrix reading a plan named by a letter; a cache-proof eager glob in the shell's tests (V2); a test that every tracked shell script keeps its executable bit (V2's review).

## 5. How we build

As in S3 from T10a: each task was one Claude Code session from its section in `docs/prompts/V.md` (ADR-0023), on its own branch from main, never stacked, and reached main through a pull request with every check green (ADR-0055). The design session reviewed the pull request with its report, answered with its acceptance or with corrections, and the acceptance was pasted on the pull request before its merge commit. Every test names what it proves (ADR-0044).

## 6. Decisions recorded

Three ADRs, each accepted at its review: 0057 (content visibility and visibility tags, V1), accepted with the model check's refusal of a business record whose table has no `visibility_tags` column; 0058 (the eProcurement Suite's modules, their waves and their licensing, and Requests and Intake's buying model as configuration, EPS-modules, beside V), accepted with its fourteen defaults decided and three of them changed; and 0059 (suites per tenant, plans per suite and what a unit loads, V2), accepted without change. V0 changed no design: ADR-0055 gained two dated paragraphs, on exceptions with true reasons and on images pinned by digest.

## 7. Closing notes (2026-10-31)

V closed with every task's "done when" met on the laptop and the proofs of section 1 in place. On main after V-close's merge: `make verify`, `make smoke TENANT=echo`, `make e2e TENANT=echo` and the release run, and the tag `v-done`.

### What differed from the plan, and why

- **V0 stopped over the fleet image, as its section asked.** No Docker release fixed all five Go findings: `docker:29.9.2-cli` fixes four, and the fifth, CVE-2026-78669 in the Compose plugin's `golang.org/x/net`, is in every Compose release so far. The design session chose option A on 2026-10-29: keep the four until that image is two weeks old (M6, 2026-11-04), renew the fifth with a corrected reason until 2027-01-28, strip the fleet's telemetry settings from its child processes, and pin every image by its version and its digest, with Dependabot keeping them current and an architecture test holding it. The pinning reached past the findings: `postgres:19rc1` had been rebuilt under the same tag since the laptop pulled it, which a digest makes visible, and V0's review moved the images still pinned by tag only (the Dockerfiles' syntax lines, the Testcontainers images, the scanners) to M6.
- **The storage of tags was measured, and the measurement reversed the plan's default.** V1's section proposed one kernel table joined in the query, as record shares are, unless a column measured clearly faster. On 120,000 records a column on each record's table answered a list page in 0.14 to 0.38 ms against 19 to 47 ms, and the list's total in 15 to 22 ms against 40 to 72 ms, so every business record carries its tags in its own table. The review then made the column a rule the model check holds.
- **Sample records in units marked for testing.** No product had a business record, and the smoke and the browser tests needed one in a running unit: the design session chose, in V1's session, a sample module loaded only where the operator marks a tenant for testing (`make sample-records`), so no customer's unit ever loads it.
- **Option B for the suite.** The owner chose to declare the eProcurement Suite for real in V2, with Purchasing as a skeleton whose one function is its About page, over a test-only module until PR0, so that the laptop and the pipeline prove installation end to end in the slice about it and not inside the first business slice. It found what a test module would not have: the seeds a unit holds differ per set of installed suites, a removed suite's schema stays and must not fail the installation record, and the unit's suites belong in its environment, not its secret, so that a change recreates its containers.
- **Two documentation tasks in between.** The owner reviewed the eProcurement Suite's module list against the leading suites on 2026-10-29, and EPS-modules wrote it, with the Requests and Intake decisions: desktop buying, central buying and the mix as configuration, the more controlled setting winning when two disagree (ADR-0058, pull request #29). It changed no product code; V2's suite and its plan follow its licensing, Purchasing in every plan of the suite.
- **Defects the laptop and the checks found**, each fixed in its task with a test where one could hold it:
  - V0: the security gate's test read the exceptions' register as of a fixed day, before the new approval (it now reads it as of its latest approval); Npgsql counts SQL state 55000 as transient, so the migration's refusal read as a passing failure; it now raises P0001;
  - V1: T9b's password rotation test counted its requests from before the pool's clear and failed on a warm pool (44d5563); the model check read the CLR's names until every caller built the model with the host's options (e9c576c);
  - V2: suite plans that other tests saved in the shared database made "the suite's only plan" ambiguous in a test; editing two scripts through the Windows path of WSL dropped their executable bit (82033b0); a stale Vitest cache hid the new suite's folder from the placement test; echo's Subscription page test expected no licensed module;
  - V-close: the matrix read only the plans named S<n>, so V's plan had never been held to its criteria; `init-env.sh` had no executable bit; V2's stale cache came from a typed glob that Vitest's own rule could not see.
- **Decisions taken in review**, beyond what the sections asked, are in each task's row of `docs/STATUS.md`: the x/net exception's expiry and ADR-0055's dated paragraphs (V0), the model check's refusal (V1), the fourteen defaults with three changes (EPS-modules), and the test that every script keeps its executable bit (V2).

### What the method learned

- A section that says when to stop and report options, rather than build, cost one round trip in V0 and left a choice that was the owner's with the owner.
- A measurement the brief asks for can change the brief's own default (V1's storage); the script stays in `tools/measurements/` for the next such choice.
- A real module finds what a fixture does not: Option B found the seeds per composition, the kept schema and the environment over the secret before any business record depended on them.
- A tool's cache is state too: Vitest's module cache hid a new folder, and the fix was in how the test wrote its call, so the cache could not hold it again.
- What a person must remember after an edit through the Windows path, a test now remembers.

### Open items carried forward

From STATUS, each with its home:

- **Into S4** (its plan's section 4): notifications and digests that name a record go only to people who may read it, tags included; attachments, exports and imports of records through the record policy; a suite's templates, events, jobs, strings and forms only where the suite is installed; the seeds per composition in S4's reference data.
- **Into S5**: TF-243, approvals and tasks that respect visibility tags, with the workflow engine.
- **Into S6**: deleting a removed suite's data at a tenant's offboarding (TF-004).
- **Into PR0 and the products**: the suite's definition leaves the Purchasing skeleton with its first masters; each product's PRD names the records that take their master's tags (rule 6), and a product that finds records by a tag adds an index on `visibility_tags`.
- **With a third suite**: the seeds hashed per suite and combined, rather than per composition.
- **Dated**: M6 on 2026-11-04 (the fleet on `docker:29.9.2-cli` and the remaining digests); the x/net exception until a fixed Compose release is two weeks old; PostgreSQL 19's final release once it is two weeks old.
- **The experience foundations slice after S5**: the accessibility scan with the browser's forced colours on.
