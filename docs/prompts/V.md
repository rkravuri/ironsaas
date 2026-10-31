# V: content visibility and suites per tenant

The instruction file for slice V (`docs/slices/V-visibility-and-suites.md`, ADR-0023). One section per task. **How to use:** start a fresh Claude Code session in `~/foundation` inside WSL, with a clean working tree on `main` and no unmerged `slice/*` branch, and paste the section's opening message. Claude Code commits the prompt first, writes a short plan into the session, builds on its branch without waiting, opens a pull request, and stops with a report. Bring the report to the design session; the answer is one message: accept and merge, or stop and adjust.

Slice V builds two kernel mechanisms that every suite needs before its first business record exists: visibility tags (TF-241) and suites per tenant (TF-244). It starts with the chores that S3 left for it.

| Task | What it builds | Requirements |
|---|---|---|
| V0 | Chores: the fleet image's five findings before their exceptions run out on 2026-11-28; dropping the old factor-secret column | |
| V1 | Content visibility: visibility tags on people and records | TF-241 |
| V2 | Suites per tenant: a customer gets the suites it buys | TF-244 |
| V-close | Documentation, exceptions and closing notes | |

---

## V0. Chores before slice V

### Opening message

```
Read CLAUDE.md, docs/STATUS.md, docs/slices/V-visibility-and-suites.md, docs/security/exceptions.md
and security/exceptions.yaml, docs/adr/0003, 0054 and 0055, the fleet's Dockerfile, and the
Identity module's factor-secret migration of T9b. Then read docs/prompts/V.md, and commit it alone
first on a new branch slice/v0-chores from main. Then follow section V0: write a short plan into the
session, and build without waiting for approval unless the "When to stop" rule in CLAUDE.md applies,
or item 1 below says to stop. Done when: V's task table is filled; the fleet image's five findings
are fixed and their exceptions removed, or the options are reported for the design session to
choose; the old factor-secret column is dropped after every unit is proven moved; make verify passes,
and the change merges through a pull request. Finish with the report and its Risk section. Then stop.
```

### Instructions

1. **The fleet image's five findings.** The Docker command-line tool and the Compose plugin in the fleet's image carry five Go findings, whose exceptions run out on 2026-11-28.
   - If fixed releases exist, upgrade to them, pinned exactly (at least two weeks old, unless the fix is newer and the finding is critical; record that), rebuild the image, scan it again, and remove the five exceptions.
   - If no fixed release exists, **do not build anything yet.** Stop and report the options with their effort and risk. For example: building the tools with a patched Go toolchain; or removing the command-line tools from the image by driving Docker through its API. The second is a design change, so the design session chooses.
2. **The old factor-secret column.** T9b moved the authenticator secrets to the tenant's data key and left the old column in place until every unit was updated. Prove that no unit still reads or holds a value in it (on the laptop: acme, bravo, echo and smoke), then drop it with a migration. A unit that has not been updated must refuse to start rather than lose its secrets; test that.
3. **V's task table** in the slice plan, from this file's table, with a State column.

Documentation: an ADR only if item 1 changes the design; the security exceptions; STATUS.

### Review checklist

- [ ] The five findings are fixed with their exceptions removed, or the options are reported, not chosen.
- [ ] No unit loses its authenticator secrets when the column goes.
- [ ] The change merges through a pull request.

---

## V1. Content visibility: visibility tags

Sharpened on 2026-10-29 after S3. TF-241: visibility tags narrow what a person sees, after roles, permissions and units. Administrators define tags (APAC, North America, a project); people and business records carry them; a person sees a tagged record when they share at least one tag with it. It is a kernel module, `Iron.Kernel.Visibility` with schema `kernel_visibility` (ADR-0029), and it must exist before the first business record type, so that every business module has tags from its first day.

The six rules, as the owner decided them:

1. Administrators define the visibility tags.
2. Master data and transactions can carry tags. A record without tags is visible to everyone who passes roles and units.
3. A person can hold tags. A person without tags sees every record.
4. A person sees a tagged record when they share at least one tag with it.
5. A person can only put tags they hold on a record.
6. Transactions may take their tags from their master; each product says how.

Three decisions shape it:

- **The filter is one more narrowing step inside the record policy's query** (ADR-0032), after the scope rule and the module's own policy. It never widens what a person sees, and lists keep their right totals because the filter runs in the database, in the same query.
- **Tags on records live in one kernel table, joined in the query,** as A8's record shares do (`kernel_data.record_shares`), unless a measurement shows a column per module table is clearly faster. Measure both on a large sample and record the choice.
- **Rule 6 is an extension point, not a kernel rule.** The kernel declares how a record type names the master it takes its tags from; each product decides when, in its own PRD.

### Opening message

```
Read CLAUDE.md, .claude/rules/backend.md, .claude/rules/frontend.md, docs/STATUS.md,
docs/slices/V-visibility-and-suites.md, docs/adr/0029, 0032, 0037, 0038, 0039, 0043 and 0051, and
TF-241, TF-035, TF-037, TF-038, TF-039 and TF-222 in docs/prd/foundation/technical-foundation-PRD.md.
Then read src/Kernel/Iron.Kernel.Data (BusinessRecord, the policy engine, UnitVisibility, the record
shares, the sample module), src/Kernel/Iron.Kernel.Authorization (the explanation, access reviews,
assignments), src/Kernel/Iron.Kernel.Audit (tracked changes), the catalogue, and in the shell the
console, the user record and the record template. Then read docs/prompts/V.md and add section V1
from docs/prompts/V-V1.md, delete that file, and commit the prompt alone first on a new branch
slice/v1-visibility from main. Then follow section V1: write a short plan into the session, and
build without waiting for approval unless the "When to stop" rule in CLAUDE.md applies. Done when:
administrators define tags; people and business records carry them; the six rules hold, each proven
by a test; the filter runs inside the record policy's query after roles and units, only narrows,
and keeps paging totals right; tag changes on people and records are tracked and audited; the access
explanation shows the tag check; access reviews list the tags a person holds; the storage choice is
measured; every criterion of TF-241 has a test; every new test names what it proves; make verify
passes, make smoke TENANT=echo and make e2e TENANT=echo pass, and the change merges through a pull
request. Finish with the report, its Risk section, the matrix's counts for TF-241, the brief's six
questions for the pages you changed, and screenshots of them in both schemes. Then stop.
```

### Instructions

1. **The module.** `Iron.Kernel.Visibility`, with its schema `kernel_visibility`, its permissions (`visibility.tags.read`, `visibility.tags.manage`, and `visibility.assignments.manage` for giving tags to people), its catalogue entries, and its threat model (T10a's check requires one).
2. **Tags** (rule 1). A tag has a code, a name, a description and a status (active or retired). Administrators create, rename, retire and reactivate tags. A retired tag stays on the records and people that carry it, so nothing becomes visible by surprise, but it can no longer be added. Every change is audited and tracked.
3. **People's tags** (rule 3). A person holds zero or more tags. Holding none means seeing every record. Giving a person a tag needs `visibility.assignments.manage`, follows A2's escalation rule (an administrator can give only tags they hold themselves, unless they hold none), is tracked, and appears in the person's security activity. Service accounts may hold tags too.
4. **Records' tags** (rules 2 and 5).
   - Every business record type can carry tags. A record without tags is visible to everyone who passes roles and units.
   - A person can put on a record only tags they hold. A person who holds none can put any active tag. A refusal names the tag.
   - A record's tags are part of its tracked changes (T7), so the History tab shows them.
5. **The filter** (rule 4).
   - A step of the record policy engine (ADR-0032), applied after the scope rule and the module's policy, for lists and for reads by identifier alike.
   - A person who holds tags sees a record when it has no tags, or shares at least one tag with them. A record outside their tags answers not found, as A3 does.
   - It only narrows, never widens, and it runs inside the same database query, so paging totals count only what the person may see (AC6).
   - **Storage:** measure the kernel join table against a tag column per module table, on at least 100,000 records with realistic tag counts, for a list page and a read by identifier. Choose and record the result in the ADR.
6. **Rule 6, the extension point.** The kernel declares how a record type names its master for tags (for example, an order's supplier). When a product's rule says so, a new record takes its master's tags when it is saved. Prove it on the data tests' sample module, with a sample transaction taking a sample master's tags (AC8).
7. **The access explanation** (ADR-0039). The explanation of a person's access to a record names the tag check, with the tags compared, in words (AC2).
8. **Access reviews** (A7). A review's items list the tags each person holds (AC7). A review can also have tag holders as its subject, so a reviewer can confirm or remove a person's tags.
9. **The shell.**
   - A "Content visibility" page in the console: the tags (create, rename, retire, reactivate) and, for each tag, who holds it.
   - A "Visibility" tab on the user record: the person's tags, with add and remove.
   - A tags control on the record template, for any record type that carries tags, showing the record's tags and letting a permitted person change them within rule 5.
   - Every string in the catalogue; the scan covers every new page and control in both schemes.
10. **Tests.** Each names its criteria, and every criterion of TF-241 is covered. Also: the escalation rule for giving tags; a retired tag still filtering but not addable; tags on the History tab; the filter combined with a share (A8) and with the working unit (A8), never widening either; a service account with tags; the measurement's numbers in the report. The smoke gains a step that tags a sample record for a person of its run and proves they see it and an untagged one, and not one with another tag. The browser tests cover the console page and the record's tags control.
11. **On the laptop.** `make images`, `make up-control`, `make update-unit` for acme, bravo and echo, `make smoke TENANT=echo`, `make e2e TENANT=echo`.

Documentation: the next free ADR (the filter's place in the policy engine; the storage measurement and choice; retired tags; rule 5 and the escalation rule; rule 6 as an extension point); the Visibility README; a "Visibility tags on a record type" section in the developer guide, for the business modules to come; the threat model; STATUS.

### Review checklist

- [ ] All six rules hold, each with its test.
- [ ] The filter only narrows, runs in the same query, and keeps paging totals right.
- [ ] Nobody gives a tag they do not hold, unless they hold none.
- [ ] A retired tag never makes a record suddenly visible.
- [ ] The explanation and access reviews show tags.
- [ ] The storage choice is measured, not assumed.

---

## V2. Suites per tenant, with the eProcurement Suite's skeleton

Sharpened on 2026-10-29 after the module decision. TF-244: a customer gets the suites it buys. The Foundation is always installed; a suite's modules are migrated, seeded and loaded only for tenants that bought it; adding a suite is an update job, and removing one switches its modules off and keeps their data. Inside a suite, modules and features are entitlements (A4).

The owner chose **Option B**: the eProcurement Suite is declared for real now, with its first module, **Purchasing** (module 1 of the suite's list, which PR0 fills later), containing no business data yet. Its one real function is an **"About this suite"** page and endpoint that show what is installed for the tenant, and under which plan. That lets the laptop and the pipeline prove suite installation end to end, in the slice about exactly that, instead of discovering its problems inside the first business slice.

### Opening message

```
Read CLAUDE.md, .claude/rules/backend.md, .claude/rules/frontend.md, docs/STATUS.md,
docs/slices/V-visibility-and-suites.md, docs/adr/0018, 0029, 0034, 0043, 0055 and the ADR of V1,
TF-040 to TF-046 and TF-244 in docs/prd/foundation/technical-foundation-PRD.md, and
docs/prd/suites/procurement/README.md and purchasing-PRD.md. Then read
src/Kernel/Iron.Kernel.Tenancy (plans, tenant entitlements, the catalogue),
src/Kernel/Iron.Kernel.Fleet (provisioning, update jobs, the unit runtime and its template),
src/Kernel/Iron.Kernel.Entitlements, src/Kernel/Iron.Kernel.Api (IModule, module registration),
src/Hosts, src/Suites/ and its READMEs, tests/Iron.Architecture.Tests (the suite rules of L2), and in
the shell src/modules (registry, the suite folders), the home page and /me/context. Then read
docs/prompts/V.md and add section V2 from docs/prompts/V-V2.md, delete that file, and commit the
prompt alone first on a new branch slice/v2-suites from main. Then follow section V2: write a short
plan into the session, and build without waiting for approval unless the "When to stop" rule in
CLAUDE.md applies. Done when: the eProcurement Suite is declared with its Purchasing module, its
schema and its About page; a tenant provisioned with the suite gets its schema, endpoints and page,
and a tenant without it gets none of them; adding the suite to an active tenant is an update job,
and removing it switches its modules off and keeps the data; plans exist for the Foundation and per
suite, and a plan of one suite cannot name another's module; every criterion of TF-244 has a test;
on the laptop echo has the suite and bravo does not, then bravo gains it and loses it again; the
release's integration stage provisions a tenant with the suite; make verify passes, make smoke
TENANT=echo and make e2e TENANT=echo pass, and the change merges through a pull request. Finish with
the report, its Risk section, the matrix's counts for TF-244, the brief's six questions for the
About page, and screenshots of it in both schemes. Then stop.
```

### Instructions

1. **The suite and its first module.**
   - The eProcurement Suite, short name `procurement`, declared in code with its display name and description.
   - `Iron.Suites.Procurement.Purchasing` in `src/Suites/Procurement/`, layer `Suite`, suite `Procurement` (ADR-0043), schema `procurement_purchasing`. No business entities yet; one table records the suite's installation in this tenant (installed version, installed at, by which job). PR0 fills the module later.
   - Its permission for the About page, for example `purchasing.suite.read`, given by the seed to the tenant administrator role.
   - Its threat model, as T10a's check requires.
2. **The About page and endpoint.** `GET /api/v1/suites/procurement/about` answers what is installed for this tenant: the suite, its version, when and by which job it was installed, its plan, its modules with their entitlement state, and its features. In the shell, an "About this suite" page under the suite's group on the home page, in the console template, read-only. It is a real function: an administrator can see exactly what the tenant has.
3. **Plans: the Foundation and each suite** (TF-244).
   - Kernel features such as `authorization.role_transfer` belong to a **Foundation plan** that every tenant has (today's Standard and Enterprise become Foundation plans).
   - Each suite has **its own plans**. Seed one for the eProcurement Suite (for example "eProcurement Essentials") listing the Purchasing module. Later modules join the suite's plans as they arrive.
   - A tenant has one Foundation plan and, per suite it bought, one suite plan with its dates. A suite plan that names a module of another suite, or of the Foundation, is refused (the TF-244 error case).
   - Record in the ADR how the Foundation plan and suite plans combine into the tenant's entitlements.
4. **Installing a suite per purchase** (TF-244).
   - The control plane records each tenant's suites. Provisioning takes the suites to install. `make provision TENANT=x SUITES=procurement` and `make suite-add TENANT=x SUITE=procurement PLAN=...` and `make suite-remove TENANT=x SUITE=procurement`, each audited with the operator and a reason.
   - The unit's configuration lists its suites. At start, a unit registers the modules of the Foundation and of its suites only; a suite it does not have maps no endpoints, has no schemas, and sends no shell modules.
   - Adding a suite to an active tenant is an update job: migrate and seed the suite's modules, then restart the unit with the suite in its configuration.
   - Removing a suite is an update job too: the unit restarts without it, so its endpoints and pages are gone, and its schemas and data stay. Adding it again later finds the data where it was.
   - A suite plan's end date and grace work as A4's entitlements do: an expired suite's modules are switched off, not uninstalled.
   - Images: one image per release carries the Foundation and every suite (ADR-0043). The installation record (T8) names the tenant's suites.
5. **The shell.** `/me/context` names the tenant's suites. The home page groups openers by suite (L2), and shows the eProcurement Suite's group only where it is installed and entitled. Every string in the catalogue; the scan covers the About page in both schemes.
6. **Tests.** Each names its criteria, and every criterion of TF-244 is covered:
   - a tenant with the suite has its schema, its endpoint and its page; a tenant without it has none, and the endpoint answers not found;
   - adding the suite to an active tenant migrates, seeds and loads it; removing it keeps the schema and its rows; adding it again finds them;
   - a suite plan naming another suite's module is refused;
   - a suite's expiry switches its modules off within a minute, with A4's grace;
   - the Foundation never references a suite (L2's architecture tests still pass);
   - the smoke gains a step that reads the About endpoint on echo; the browser tests open the About page; the release's integration stage provisions its tenant with the suite.
7. **On the laptop.** `make images`, `make up-control`; give echo the suite (`make suite-add TENANT=echo SUITE=procurement ...`), leave bravo without it, then add it to bravo, check the About page, remove it and check that the data stays; `make update-unit` for acme; `make smoke TENANT=echo`, `make e2e TENANT=echo`.

Documentation: the next free ADR (Option B and why; the Foundation plan and suite plans; installing, removing and expiring a suite; what a unit loads at start); the Suites and Purchasing READMEs; the runbook (`suite-add`, `suite-remove`, provisioning with suites); STATUS.

### Review checklist

- [ ] A tenant without the suite has no schema, endpoint or page of it.
- [ ] Removing a suite keeps its data, and adding it again finds the data.
- [ ] A suite plan cannot name another suite's or the Foundation's module.
- [ ] The About page shows exactly what the tenant has.
- [ ] The pipeline's tenant is provisioned with the suite.

---

## V-close. Exceptions, documentation and the closing notes

Sharpened on 2026-10-30 after the module decision. Slice V built visibility tags (TF-241) and suites per tenant with the eProcurement Suite's skeleton (TF-244), after its chores (V0). It also carried two documentation tasks: the eProcurement Suite's module list (EPS-modules) and the Requests and Intake decisions. This task makes V a done slice in the matrix, leaves the documentation true for V, and readies S4.

### Opening message

```
Read CLAUDE.md, docs/STATUS.md (all of it, the later items twice),
docs/slices/V-visibility-and-suites.md, docs/slices/S4-platform-services.md, docs/trace/exceptions.md,
docs/adr/README.md, the README, docs/guides/developer-guide.md, docs/guides/local-stack.md,
docs/guides/definition-of-done.md, docs/licences.md and docs/prd/suites/procurement/README.md. Then
read docs/prompts/V.md and add section V-close from docs/prompts/V-close.md, delete that file, and
commit the prompt alone first on a new branch slice/v-close from main. Then follow section V-close:
write a short plan into the session, and build without waiting for approval unless the "When to
stop" rule in CLAUDE.md applies. Done when: V is a done slice in the matrix, with every criterion of
TF-241 and TF-244 tested or listed as an approved exception with its home; every STATUS item marked
for V is done or moved with a reason; the README, the runbook, the developer guide, the definition of
done and the ADR index are true for V; the V plan has its coverage table and closing notes; S4's plan
lists what V moves to it; the change merges through a pull request; make verify, make smoke
TENANT=echo, make e2e TENANT=echo and an on-demand release on main pass, and main is tagged v-done.
Finish with the report, its Risk section, the exceptions with their homes for approval, and what
moves to S4. Then stop.
```

### Instructions

1. **V as a done slice.** Mark V done in its plan's requirement list, so `make trace` treats it as done. Every criterion of TF-241 and TF-244 has a test, or a row in `docs/trace/exceptions.md` with its reason and its home. List each exception for the design session's approval.
2. **STATUS.** Every later item marked for V is done, or moved with a reason. Keep the dated items as they are: M6 on 2026-11-04 (the fleet image and the remaining digests), the x/net exception until a fixed Compose release is two weeks old, and the move to PostgreSQL 19's final release. Add one later item from the design session: the accessibility scan also opens every gallery page with the browser's forced-colours mode on (Windows contrast themes), and fails if a page refuses the user's colours; its home is the experience foundations slice planned after S5.
3. **The documentation, true for V.**
   - **README:** "What works today" gains visibility tags and suites per tenant. The "Where it stands" table shows the eProcurement Suite as declared, with its Purchasing skeleton, and links the suite's module list.
   - **Runbook:** every V make target (`sample-records`, `suite-add`, `suite-remove`, provisioning with suites) and every new symptom (a tenant without a suite it expects; a record hidden by its tags).
   - **Developer guide:** "Visibility tags on a record type" and adding a module to a suite, checked for order and repetition.
   - **ADR index and definition of done:** complete for V.
4. **The V plan.** The coverage table, and the closing notes: what differed from the plan and why (V0's stop over the fleet image and the digest pinning it led to; the measured storage choice for tags; Option B for the suite; the module list and the Requests and Intake decisions written in between), and what V carries forward.
5. **S4's plan.** Its skeleton (written at S3-close) gains what V moves to it, with the reasons. S4's tasks themselves are written in the design session.
6. **The finish.** The change merges through a pull request. On main after the merge: `make verify`, `make smoke TENANT=echo`, `make e2e TENANT=echo`, and the release workflow started on demand, which must pass. Then tag `v-done` on the merge commit and push the tag.

### Review checklist

- [ ] V is done in the matrix, with every gap an approved exception with its home.
- [ ] The dated later items are kept, and the forced-colours scan is recorded.
- [ ] The README shows the eProcurement Suite as declared, without claiming what is not built.
- [ ] The closing notes and S4's additions exist.
- [ ] The release on main passes, and `v-done` is tagged.
