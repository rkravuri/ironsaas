# Status

The running state of the current slice, kept current by Claude Code at the end of every session. The slice plan is `docs/slices/S2-access-control-organisations-and-design.md`; the tasks are in `docs/prompts/S2.md`.

## Slice S2

S2 is done: every task merged, the done-when of the plan's section 1 proven, the closing notes written (2026-10-20, tag `s2-done` on 845b902).

| Task | State | Branch | Notes |
|---|---|---|---|
| D1 Design foundation | done | main (f145e5f) | accepted 2026-10-18; six commits plus the State column |
| D2 The drawn world | done | main (fd56702) | merged 2026-10-18; six commits plus the figure correction from the review |
| D3 Templates, panels and forms | done | main | merged 2026-10-18 (110c6ef); the scan, format and the keyboard test on the stack passed on 2026-10-19 |
| P1 The laptop store survives a restart, and an update heals a unit | done | main | merged 2026-10-19; five commits (5c24448 to b044602) |
| L1 Three layers: kernel, business, products | done | main | merged 2026-10-19 (7af58bf); twelve commits (a304e52 to 0ff8305); ADR-0029; the laptop reset, acme, bravo and echo provisioned again, smoke 11 of 11 and e2e 6 of 6 on echo |
| A1 Organisation units | done | main | merged 2026-10-19 (4004aa3); seven commits (ba5be84 to 077aca4); ADR-0030; acme, bravo and echo updated with their root units, smoke 11 of 11 and e2e 6 of 6 on echo |
| A2 Roles and scoped assignments | done | main | merged 2026-10-19 (05a34eb); nine commits (0457c7f to 1d9f7b5); ADR-0031; acme, bravo and echo updated, smoke 11 of 11 and e2e 8 of 8 on echo, with a child-unit administrator |
| A3 Record policies | done | main | merged 2026-10-19 (1af5b18); seven commits (c813656 to 49e3f70); ADR-0032; acme, bravo and echo carry A2 and A3, smoke 11 of 11 on echo |
| D4 Home and the header | done | main | merged 2026-10-19 (179fbfa); seven commits (79cb00f to 096dff9); ADR-0033; acme, bravo and echo carry D4, smoke 11 of 11 and e2e 11 of 11 on echo |
| A4 Entitlements | done | main | merged 2026-10-20 (94ad0d3); seven commits after the prompt (8879856), 33bcea1 to 465c70e; ADR-0034; the refresh question closed: 30 seconds, accepted; the shell reads /me/context only while its tab is visible; acme, bravo and echo on the Enterprise plan, smoke 11 of 11 and e2e 14 of 14 on echo at review |
| A5 Feature flags | done | main | merged 2026-10-20 (999abbf); six commits after the prompt (adccccb), 67031f7 to 83bbd1b; ADR-0035; the permission names accepted (`tenancy.flags.*`, `tenancy.kill_switches.manage`); a kill switch is on wherever any applicable rule says so, and a rule that says false is refused; the last-known rows of flags and entitlements are one upsert each; the import kill switch on echo worked both ways within a minute at review, smoke 11 of 11 and e2e 15 of 15 on echo |
| A6 Settings | done | main | merged 2026-10-20 (04408a4); six commits after the prompt (ce511f5), 891a27a to ce0564a; ADR-0036; the permission names kept (`settings.values.read`, `settings.values.manage`); an invitation is valid for 3 days by default, TF-018's 72 hours; the control plane and acme, bravo and echo carry A6, smoke 11 of 11 and e2e 18 of 18 on echo |
| A7 Delegation and access reviews | done | main | merged 2026-10-20 (30a4784); eleven commits after the prompt (1726056), 80094ad to 2566b73; ADR-0037; at review a person's own delegations moved to My profile and the console's Delegations page is for administrators, nobody decides their own access in a review (an item nobody else may review goes to the campaign's owner), the directory answers a name and a home unit a page at a time, and the shell suite became reliable (`make shell-check` three times in a row beside the running stack); `make up-control` migrates and seeds the control plane before it starts; the control plane, acme, bravo and echo carry A7, smoke 11 of 11 and e2e 19 of 19 on echo |
| A8 Cross-organisation access and lifecycle | done | main | merged 2026-10-20 (6bb5b11); eight commits after the prompt (106fc85), 254ce0a to 67411c1; ADR-0038; sharing modes with the shares in `kernel_data`, the cross-unit read audit, the working unit with "All my units", reactivation, readiness from modules, merging with a preview; at review the scans proved the keyboard paths of the merge and sharing dialogs, the sharing dialog's units became nested lists, and the merge preview names record types by their display name; the control plane, acme, bravo and echo carry A8, smoke 11 of 11 and e2e 21 of 21 on echo |
| A9 Explanation and segregation of duties | done | main | merged 2026-10-20 (bc09285); eight commits after the prompt (a5e6a8e), 45543f3 to 5bd9dd1; ADR-0039; the explanation from the gates' own decisions, with a matrix test against the real call; conflict rules by overlapping scope on grants, delegations and role changes, with a daily report and the review marker; the console's Explain access and Conflict rules, and the grant dialog's conflict in words; at review a role's change became checked for everyone holding the role (a block rule refuses it with the rule and the number of people, a warn rule saves it and the report lists the new conflicts), and an allowance says the action may still refuse for its own reasons; make test 755 on main (the first run lost the Data tests' fixture to a Testcontainers regex timeout under load, the second passed), shell-check 228 tests, the scan 60 of 60; the control plane, acme, bravo and echo carry A9, smoke 11 of 11 and e2e 25 of 25 on echo |
| S2-close | done | main | merged 2026-10-20 (845b902); eighteen commits after the prompt (e901b9e), f30e5da to 4fefd53, one of them a temporary trigger for the pipeline, removed again; ADR-0040; the smoke's three steps for a scoped role, a plan change and a kill switch, each restoring the tenant; every browser test run in a unit of its own; the .NET suite on three shared services, four projects at a time (peak 8.6 GB against 10.0, 23 containers against 44); Prettier for the shell, at a width of 120 since the review; the search a combobox a screen reader follows; the scan in a folder of its own; the workflows on current actions, the images naming a non-root user, Dependabot held to two weeks and its pull requests closed with their reasons; the documentation, the closing notes and the S3 skeleton. On main: make test 758, make shell-check 229 tests, the scan 60 of 60, make format clean; the control plane, acme, bravo and echo carry S2-close, smoke 14 of 14, e2e 25 of 25 and integration 14 of 14 on echo |

## Slice S3

S3 is next. The plan's skeleton is `docs/slices/S3-trust-and-compliance.md`; the tasks are written in the design session, into `docs/prompts/S3.md` and this table, now that the PRD split is merged.

| Task | State | Branch | Notes |
|---|---|---|---|
| | | | |

## The PRD split

The Functional Foundation PRD split into one document per layer and product (ADR-0041), from `docs/prompts/PRD-split.md`, before the business and product slices and S3's tasks.

| Task | State | Branch | Notes |
|---|---|---|---|
| PRD split | done | main | merged 2026-10-20 (9180b07); seventeen commits after the prompt (3012bfb), 61f01b5 to e9054c1; ADR-0041 and ADR-0042. The map has 172 rows: 154 moved, 9 split, 9 covered by the kernel, none merged or retired. The kernel PRD holds 207 requirements, TF-241 to TF-243 new; the business and product documents 208 (Business 59, Supplier management 18, Contracts 10, Catalogue 12, Requests 15, Orders 21, Receiving 23, Invoicing 30, supplier portal 14, product method 5), and five one-page scopes. At the checkpoint the design session answered seven points (Business never depends on a product; products in process order, ADR-0042; the module names; the reconciliation per module; TF-243; bill-to as a location type; portal ownership as SPX-006); at review it decided every `[Decision]` (each marked "decided 2026-10-20"), retired BF-025, accepted the six proposals and section 7.2's order in principle, and corrected ADR-0042: a person's conversion is a call from the later product's screen, only automatic ones are events. `make prd-check` (16 tests, then the check) runs in CI. On main: make prd-check passed, make test 758, make shell-check 229 tests, make format clean |

The Business PRD corrects the A1 amendment in `docs/prompts/S2.md`: a purchasing group is a master, not a unit kind, because it serves several units and owns no records (BF-007, since the review of L2 the eProcurement Suite's PUR-001). `src/Business/README.md` and the comment on `IOrganisationKinds` say so too.

## IronSaaS Foundation and suites

The third layer became Suite (ADR-0043), from `docs/prompts/L2-suites.md`: IronSaaS Foundation is the kernel and business layers together, and the eProcurement Suite (`procurement`) is the first suite on it.

| Task | State | Branch | Notes |
|---|---|---|---|
| L2 IronSaaS Foundation and suites | done | main | merged 2026-10-20 (e5a05af); ten commits after the prompt (2696cd6), d4199f4 to 6f792c6, the last three the review's changes; on main: make prd-check passed, make test 822, make shell-check 235 tests, make shell-a11y 60 passed, make format clean; ADR-0043. The Suite layer in the build (`IronSuite` beside `IronLayer`, `IRON0003` and `IRON0004`, the suite carried into the assembly's metadata) and in the architecture tests, with fixtures for a reference between suites, from the Foundation to a suite, and a name, folder or schema prefix of another suite; `src/Suites/` with its README and the eProcurement Suite's; `make ef-add` finds a suite module by its last segment; `make catalogue` groups by suite, the Foundation first (format version 2); the shell's `modules/suites/`, refused to kernel and business modules, the local lint rule `iron/suite-imports` between suites, a manifest's `suite` held to its folder, and the home page's openers grouped by suite; the requirements in `docs/prd/foundation/` and `docs/prd/suites/procurement/` with every identifier unchanged, prd-check refusing a dependency between suites; TF-244 in the tenancy area and slice V; the README, SECURITY.md and the names. At review, 2026-10-20, the design session accepted it with six changes, made before the merge: ADR-0043's decision 8 (the Foundation holds mechanisms and generic masters, a suite brings its own content); the purchasing PRD (`PUR-`) for the shared master `Iron.Suites.Procurement.Purchasing`, with purchasing groups, the purchasing profiles of suppliers and people, the starter approvals and the suite's readiness items; BF-007 and BF-201 moved whole and BF-021, BF-061, BF-101, BF-106, BF-107, BF-108, BF-161, BF-162, BF-164, BF-202 and BF-203 split, each moved part keeping its old identifiers in `was` and listed in the map's section "Moved after the split", which prd-check holds; each product names its templates and declares its notification keys; the shell's header reads IronSaaS; SECURITY.md sends reports to the security address. |

Decided in the task, each small enough for a line here:

- `Foundation` is not a suite name either, beside `Kernel` and `Business`: `foundation` is the catalogue's and the home page's name for everything outside a suite.
- A module's suite is its project's `IronSuite`, written into the assembly by `Directory.Build.props` and read by `IModule.Suite`'s default; the architecture test refuses a module that reports another.
- The catalogue's file went to format version 2 and was written fresh from the code; it held no retired flag, so nothing was lost. In memory its lists stay flat, so no reader changed.
- The runtime schema check accepts a suite's prefix only for the suites its caller's contexts belong to; the kernel names no suite.
- The home page keeps "Where to go" and groups inside it, each group under its suite's name (a third-level heading); with only the Foundation's modules today, every tenant sees one group, "IronSaaS Foundation".
- The PRDs that moved carry version 1.1 with a change-log line (the technical PRD 1.4 DRAFT, with TF-244); no requirement's words changed.

Decided while making the review's changes, as readings of decision 8 beyond its list:

- BF-061's default match levels moved with the other match levels, to a legal entity's default in the purchasing master (PUR-025), which also states the order a line's match level is taken in; ORD-005 reads it.
- SUP-005's portal channels were the same setting as BF-106's, so both went to the supplier portal as one requirement (SPX-007), which writes them into the new supplier's purchasing profile; the supplier portal sits above the master, so the dependency points down.
- BF-203's enabled suppliers became the suite's readiness item, beside BF-202's suppliers and items (PUR-081).
- ORD-022 holds the acknowledgement reminder days; the receipt and return notes, the order and change order, and the invoice acknowledgement are named by ORD-141, RCV-141 and INV-121; every product declares its notification keys in a requirement of its own (SUP-121, CON-081, CAT-081, REQ-081, ORD-142, RCV-142, INV-122), each listing the keys its requirements already raised.
- The purchasing master's permission names are proposed (`purchasing.groups.*`, `purchasing.profiles.*`, `purchasing.configuration.manage`), replacing `organisations.purchasing_groups.*`, which was never built; the slice that builds the module decides them.
- The technical PRD's section 7.2 gains PR0, the purchasing master, after B4 and before PR1.

## Maintenance

| Task | State | Branch | Notes |
|---|---|---|---|
| M1 Dependabot's pull requests | done | main | merged 2026-10-20; gitleaks reads a pull request's commits (`pull-requests: read` for its job alone; every pull request's scan had failed with 403); #12's seven .NET updates taken without `Microsoft.CodeAnalysis.CSharp`, which moves with EF Core by hand; #10 (jest-dom 7) and #11 (React Router 8) merged with no code change, make shell-check and make shell-a11y passing on each and on both; TypeScript majors held until typescript-eslint accepts them |

## Open questions

None open. (Closed in S3 T1: `SECURITY.md` sends reports to `security@example.com`, the address the owner gave.)

## Later items

Carried into S3 and beyond; the S2 plan's closing notes group them by area. Settled by S2-close: each browser test run works in a unit of its own; the search announces its active result (a combobox, ADR-0033's search kept); the accessibility scan writes into `test-results/a11y`; the .NET suite runs on three shared services, four projects at a time, proven three times straight after the scan; the action pins and Dependabot's pull requests.

- A password reset flow (S3, TF-017) is also what an operator needs when a tenant's administrator password is lost; until then the smoke and the browser tests use a tenant whose administrator password the smoke kept (`secret/tenants/<slug>/smoke`).
- "Sign out everywhere" waits for the session console (TF-022); My profile offers Sign out, which ends the session in every tab of this browser (ADR-0033).
- After an answer the shell estimates the idle end from the span `/me` showed, up to the server's touch interval (one minute) late (ADR-0033). A response header with the session's idle end would make it exact if a tenant shortens the limits.
- Every refused request writes `entitlements.access_denied`; a client that keeps calling a refused endpoint fills the audit trail. The shell hides those calls; rate limits (S3) bound the rest.
- The last-administrator guard counts tenant-wide assignments, not active principals (ADR-0031): a disabled last administrator still counts.
- A record-scoped assignment does not check that its record exists: no module can name another module's records yet. TF-033 AC4 holds for units. A record-scoped assignment is not in an access review either (ADR-0037).
- No kernel module declares a business record, so no module registers a record policy yet; the first business or suite module brings the first, and the architecture test then reads its `AddServices`. A module's own Entity Framework reads for background work are not checked by the build; they must not reach a response (ADR-0032).
- System roles follow the permission catalogue when the seed runs, unchecked by the conflict rules: a new module's permissions in a system role show in the next report run, not as a refusal. A tenant role's change is checked at once (ADR-0039).
- A rule's change appears in the conflicts report at the next run, within a day, or at once with Run the report now; a record-scoped grant meets only a tenant grant or a grant on the same record in the rules; the grant dialog's conflict preview is not shown when the preview call fails, and the API still refuses a blocked grant (ADR-0039).
- The explanation covers the checks an endpoint declares; checks a handler makes on its own are not explained, and an allowance says so in words (ADR-0039).
- A delegator who loses a role loses it for their delegate at once in the process that changed it, and within the grants cache's 60 seconds in the others (ADR-0012, ADR-0037).
- The directory (`/api/v1/directory`) shows the names and home units of active people to everyone signed in, for choosing a delegate. Revisit if a tenant needs people hidden from each other.
- TF-037 AC1 (a workflow assigns the delegate too) is S5; the escalation of late reviewers is recorded and audited, not sent (S4).
- Splitting a unit (TF-223); a share with a unit and the units below it; a merge as a job for a unit with millions of records (ADR-0038). A review campaign that names a merged unit keeps naming it. The header hides the choice of unit while acting.
- Trials and their banners (TF-044), an operator's change of one entitlement without a plan, assigning seats from the console, and the counts of a removed module's data in the plan preview (TF-042 AC2) wait. No licensed module exists yet: the module checks and seats are proven with a fixture module in `Iron.Kernel.Entitlements.Tests`.
- Flag rules have no console page; exposure events (TF-053 AC2) and tenant-visible flags (TF-055) are not part of S2. Kill switches for email, webhooks, SMS, AI and push are declared but wired only by the slices that build those paths (S4 for email); only `kill.imports` is wired.
- Settings have no operator-wide override of a default, no way to write a secret into the store from the console, and no record scope (ADR-0036). A setting's name on the console comes from the shell's catalogue.
- Each unit reads its entitlements and flags from the store and its settings from the database twice a minute, and `/me/context` evaluates every flag for the person every 30 seconds: fine for today's fleet and flags, to revisit with many hundreds of units or flags (ADR-0034 to ADR-0036).
- The control plane and the fleet are no tenant: their flags come from `Flags:Rules` in configuration, global rules only.
- The Users grid has no filter on type and status; a filter that offers the words (a choice filter in `DataGrid`) is a later item.
- Testing Library names a tree item from all its content, the items below it included (Mantine's Tree gives the item no label of its own); check a parent item with a screen reader before the tree pattern spreads. jsdom hides Mantine's menus, so unit tests query them hidden or not and the browser specs prove what is visible.
- Chromium reads a visually hidden word with a space before it ("North , chosen"); the browser specs match either spacing.
- No module has a spot drawing of its own yet: the opener's slot stays empty until a module brings one through the illustration checklist.
- The ADRs and prompts before ADR-0041 cite the archived FF identifiers; `docs/prd/requirements-map.md` translates them. The tests S3 tags with what they prove use every PRD's identifiers (TF-218).
- No API disables a person or deletes a unit: each browser test run leaves its inactive `E2E run <run>` unit with its people, and each smoke run its inactive `Smoke <run>` unit with one person.
- The browser test that moves echo between Standard and Enterprise takes about a minute and a quarter and leaves echo on Enterprise, also when it fails; the smoke's `plan-change` step leaves a tenant on the plan it had.
- Since S2-close the `smoke` tenant is provisioned again on the laptop (on Standard), beside acme, bravo and echo; their administrators' invitations are in `secret/tenants/<slug>/fleet`.
- Dependabot proposes versions only after 14 days (its cooldown) and majors outside its groups. Two it is told to leave, each with its reason in `.github/dependabot.yml`: `Microsoft.CodeAnalysis.CSharp`, which must match the Roslyn that EF Core's design package pulls (5.9.0 beside EF Core 10.0.12's 5.0.0 does not restore, NU1107), so it is raised by hand with the EF Core that asks for it; and TypeScript majors, until typescript-eslint accepts them (8.72.0 asks for typescript below 6.1). React Router 8 and jest-dom 7 are in since M1.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
- TF-244 is written, not built: until it is, a unit loads every module `Iron.Host.Api.UnitModules` lists, and no suite module exists yet. A plan cannot yet be refused for naming another group's module (TF-244 AC7); the catalogue's groups are what that check will read.
- Decision 8 of ADR-0043 is applied to what the review listed. Read against it again when the second suite's PRDs are written: the business lookups and their seeds name procurement's reasons and charge types (BF-121, BF-122), and BF-005 lists procurement's settings among those set per organisation.
- On a laptop that runs the stack, `make shell-check` straight after `make test` timed out a few different page tests at Vitest's 5-second limit, twice in L2 (load average near 6); run alone, every test passed both times. CI runs the two on separate runners. Raise the limit for the page tests, or keep running the shell's checks on their own, if it recurs.
- Separate images per suite, and a suite in a repository of its own with the Foundation as packages, wait for ADR-0043's conditions (a separate team, release rhythm or licence).
