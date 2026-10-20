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

S3 is next. The plan's skeleton is `docs/slices/S3-trust-and-compliance.md`; the tasks are written in the design session, into `docs/prompts/S3.md` and this table, after the Business PRD and the product PRDs are split (ADR-0029).

| Task | State | Branch | Notes |
|---|---|---|---|
| | | | |

## Open questions

None.

## Later items

Carried into S3 and beyond; the S2 plan's closing notes group them by area. Settled by S2-close: each browser test run works in a unit of its own; the search announces its active result (a combobox, ADR-0033's search kept); the accessibility scan writes into `test-results/a11y`; the .NET suite runs on three shared services, four projects at a time, proven three times straight after the scan; the action pins and Dependabot's pull requests.

- A password reset flow (S3, TF-017) is also what an operator needs when a tenant's administrator password is lost; until then the smoke and the browser tests use a tenant whose administrator password the smoke kept (`secret/tenants/<slug>/smoke`).
- "Sign out everywhere" waits for the session console (TF-022); My profile offers Sign out, which ends the session in every tab of this browser (ADR-0033).
- After an answer the shell estimates the idle end from the span `/me` showed, up to the server's touch interval (one minute) late (ADR-0033). A response header with the session's idle end would make it exact if a tenant shortens the limits.
- Every refused request writes `entitlements.access_denied`; a client that keeps calling a refused endpoint fills the audit trail. The shell hides those calls; rate limits (S3) bound the rest.
- The last-administrator guard counts tenant-wide assignments, not active principals (ADR-0031): a disabled last administrator still counts.
- A record-scoped assignment does not check that its record exists: no module can name another module's records yet. TF-033 AC4 holds for units. A record-scoped assignment is not in an access review either (ADR-0037).
- No kernel module declares a business record, so no module registers a record policy yet; the first business or products module brings the first, and the architecture test then reads its `AddServices`. A module's own Entity Framework reads for background work are not checked by the build; they must not reach a response (ADR-0032).
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
- PRD section 2.1 and the requirement texts keep the S1 module names, pointed to ADR-0029 from the document control; the PRD split in the design session renames them and divides `Iron.Organisations` between the kernel tree and `Iron.Business.Organisations`.
- No API disables a person or deletes a unit: each browser test run leaves its inactive `E2E run <run>` unit with its people, and each smoke run its inactive `Smoke <run>` unit with one person.
- The browser test that moves echo between Standard and Enterprise takes about a minute and a quarter and leaves echo on Enterprise, also when it fails; the smoke's `plan-change` step leaves a tenant on the plan it had.
- Since S2-close the `smoke` tenant is provisioned again on the laptop (on Standard), beside acme, bravo and echo; their administrators' invitations are in `secret/tenants/<slug>/fleet`.
- Dependabot proposes versions only after 14 days (its cooldown) and majors outside its groups; three majors it proposed need work of their own: React Router 8, jest-dom 7 and TypeScript 7.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
