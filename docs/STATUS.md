# Status

The running state of the current slice, kept current by Claude Code at the end of every session. The slice plan is `docs/slices/S2-access-control-organisations-and-design.md`; the tasks are in `docs/prompts/S2.md`.

## Slice S2

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
| S2-close | next | | |

## Open questions

None.

## Later items

- Before S2 closes: each e2e run creates its own child unit named after the run and checks only inside it, so leftovers never affect a later run. Today every run adds invited accounts to echo's one `E2E branch`, and a check that lists the unit must accept every kind a test ever left there.
- Dependabot's first pull requests bump actions to new majors; their CI runs are red until the pins are checked one by one.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
- A password reset flow (S3) is also what an operator needs when a tenant's administrator password is lost.
- Splitting a unit by reassigning its records (TF-223) is a later item; units are created, moved, deactivated, reactivated and merged (ADR-0038).
- The Users grid shows type and status in words and has no filter on them; a filter that offers the words (a choice filter in `DataGrid`) is a later item.
- The last-administrator guard counts tenant-wide assignments, not active principals (ADR-0031): a disabled last administrator still counts.
- No kernel module declares a business record (A3 step 6), so no module registers a record policy yet; the first business or products module brings the first, and the architecture test then reads its `AddServices`.
- Record policies guard paging and reads by id (ADR-0032). A module's own Entity Framework reads for background work are not checked by the build; they must not reach a response.
- `make e2e` makes a new child administrator only when the store has lost the one it made (a password cannot be set again before S3's reset), so a reset store leaves an unused account in the tenant.
- A record-scoped assignment does not check that its record exists: no module can name another module's records yet. TF-033 AC4 holds for units.
- Testing Library names a tree item from all its content, the items below it included (Mantine's Tree gives the item no label of its own); the scan passes, but check a parent item with a screen reader before the tree pattern spreads.
- Since the L1 reset (2026-10-19) acme, bravo and echo are fresh tenants and their administrators' invitations are in `secret/tenants/<slug>/fleet`; the smoke tenant was not provisioned again (`make smoke` without `TENANT` provisions it).
- "Sign out everywhere" waits for the session console (TF-022); My profile offers Sign out, which ends the session in every tab of this browser (ADR-0033).
- Before S2 closes: the search announces its active result to a screen reader. Mantine's Spotlight gives its actions as buttons the input does not point at (no `aria-activedescendant`), so a screen reader hears the query and not which action Enter will open; the scan passes, which does not prove it. Fix it inside Spotlight if it allows, otherwise with a combobox of our own.
- After an answer the shell estimates the idle end from the span `/me` showed, up to the server's touch interval (one minute) late (ADR-0033). A response header with the session's idle end would make it exact if a tenant shortens the limits (A6).
- No module has a spot drawing of its own yet: the opener's slot stays empty until a module brings one through the illustration checklist.
- Each `make e2e` invites one person into echo's `E2E branch` to prove the invitation link; those accounts stay Invited.
- jsdom lays nothing out, so Mantine hides a menu's dropdown in unit tests; those tests query the items hidden or not, and the browser specs prove what is visible.
- PRD section 2.1 and the requirement texts keep the S1 module names, pointed to ADR-0029 from the document control; the PRD split in the design session renames them and divides `Iron.Organisations` between the kernel tree and `Iron.Business.Organisations`.
- The plan preview warns that a removed module's data stays and is hidden; the counts of that data (TF-042 AC2) wait for the first module that has data.
- Trials and their banners (TF-044), an operator's change of one entitlement without a plan (status, dates, seats), and assigning seats from the console wait; seats have their API (`/api/v1/seats`) and show on the Subscription page.
- No licensed module exists yet: the module checks and seats are proven with a fixture module licensed per person in `Iron.Kernel.Entitlements.Tests`, and the Subscription page on the laptop says "No licensed modules yet".
- A tenant created before A4 has no entitlements until a plan is applied, so no role export and import; acme, bravo and echo were moved to Enterprise with `make apply-plan`. A tenant provisioned from now on starts on `standard` unless it is created with another plan, so `make smoke` without `TENANT` makes a tenant without role transfer.
- Each unit reads the store twice a minute (the API and the worker both run the Entitlements module and keep the same last-known row). Fine for the fleets in sight; revisit with many hundreds of units (ADR-0034).
- Every refused request writes `entitlements.access_denied`; a client that keeps calling a refused endpoint fills the audit trail. The shell hides those calls; rate limits (S3) bound the rest.
- The browser test that moves echo between Standard and Enterprise takes about a minute and a quarter; it leaves echo on Enterprise, also when it fails.
- Kill switches for email, webhooks, SMS, AI and push are declared and can be flipped, but nothing checks them until the slices that build those paths (S4 for email). Only `kill.imports` is wired, on role import.
- Flag rules have no console page: the operator sets them with `make flag`, `make kill-switch` or the control plane's API. Exposure events for experiments (TF-053 AC2) and tenant-visible flags (TF-055) are not part of S2.
- `/me/context` evaluates every active flag of the build for the person, every 30 seconds. Fine for the handful of flags today; with hundreds, evaluate only the flags the shell asks for (ADR-0035).
- Each unit now reads three store paths twice a minute (entitlements, `platform/flags`, `tenants/<slug>/flags`), in the API and in the worker. The same review as the entitlements' with many hundreds of units.
- The control plane and the fleet are no tenant: their flags come from `Flags:Rules` in configuration, global rules only.
- Settings have no operator-wide override of a default (the system level is the code's default; ADR-0036), no way to write a secret into the store from the console (a secret reference's path is set, the secret is put in the store by hand), and no record scope (a record takes its owning unit's value).
- Each unit process reads all of the tenant's settings twice a minute. Fine for the handful of settings today; with many thousands of values, read only what changed since the last read (ADR-0036).
- A setting's name on the console comes from the shell's catalogue (`settings.names`); a setting a module declares without one shows its key until it is named.
- Each `make e2e` invites one more person (`validity-...`) into echo's `E2E branch` to prove the unit's invitation validity; those accounts stay Invited, as the home test's do, and the child-unit test accepts both.
- A record-scoped assignment is not in an access review: a record's people are decided where the record is (ADR-0037). The shell starts a campaign over one unit with or without its children; the API takes several.
- The escalation of late reviewers is recorded and audited, not sent: the message is S4, and the reviewer's manager comes from a business module's `IReviewEscalation` (ADR-0037).
- TF-037 AC1 (a workflow assigns the delegate too) is S5. Approval, receiving and evaluation are declared as duties by the slices that build them.
- The directory (`/api/v1/directory`) shows the names and home units of active people to everyone signed in, for choosing a delegate. Revisit if a tenant needs people hidden from each other.
- A delegator who loses a role loses it for their delegate at once in the process that changed it, and within the grants cache's 60 seconds in the others (ADR-0012, ADR-0037).
- Since A6 the density follows the person, so the Users grid's browser test starts from comfortable and removes the administrator's density at the end; a test that changes a preference cleans it up the same way.
- A share lists units one by one; a share with a unit and the units below it waits until a product asks (ADR-0038).
- A merge runs in one transaction under the tree's lock. Fine for a tenant's units today; a unit that owns millions of records needs the merge as a job (ADR-0038).
- An access review campaign that names a merged unit keeps naming it, and its open items stay as they were.
- Each `make e2e` adds two units to echo's tree for the merge test (`E2E merge from` and `E2E merge into`, with the run's stamp), the first merged and inactive, and an invited person homed at the second. Together with the item on the child unit, the e2e data wants a unit of its own per run before S2 closes. The Users grid's browser test no longer looks for the smoke client's row on screen, which the accounts added by every run had pushed out of the rows the grid draws; it reads it in the API's answer.
- Echo's e2e data since A8: `E2E second branch` and `E2E third branch`, each with a colleague, and a person homed at the second who reads the users of both. They hold no role in `E2E branch`, whose access review the delegation test runs: a role there would be an item of it.
- The header's choice of unit is hidden while acting for someone, whose rights in full the lists then show (ADR-0038).
- Whoever may share a record sees the names of the tenant's active units to share it with, though they may not read the tree otherwise (ADR-0038).
- Chromium reads a visually hidden word with a space before it ("North , chosen"), as it does for "Myself , chosen" since A7, because a visually hidden element is positioned absolutely; a screen reader reads it the same. The browser specs match either spacing.
- The sharing control lists the units as checkboxes in lists nested as the tree is, not on Mantine's Tree, whose check takes the units below with it: a share lists each unit on its own.
- Settings follow the unit a person works for when they are resolved for the signed-in person; a page that resolves for someone else uses that person's home unit (ADR-0038).
- The explanation covers the checks an endpoint declares; checks a handler makes on its own (a move's new parent, a merge's target, the no-escalation rule of a grant) are not explained, and an allowance says in words that the action may still refuse for its own reasons (ADR-0039).
- System roles follow the permission catalogue when the seed runs, unchecked by the conflict rules: a new module's permissions in a system role show in the next report run, not as a refusal. A tenant role's change is checked at once for everyone holding it (ADR-0039).
- A rule's change appears in the conflicts report at the next run, within a day, or at once with Run the report now; the report is not computed again for one rule on its own.
- A record-scoped grant meets only a tenant grant or a grant on the same record in the conflict rules, not a grant at the record's owning unit (ADR-0039).
- The grant dialog's conflict preview is not shown when the preview call fails; the grant is still checked by the API, which refuses a blocked one with the rule named.
- `make test` on main after A9 once lost `Iron.Kernel.Data.Tests`' fixture to a regex timeout inside Testcontainers (`MatchImage`, parsing the image name) when it ran straight after the scan, with the machine loaded; a second run passed 755 of 755. If it recurs, run the tests apart from the scan first, then look at the fixture and the Testcontainers version.
- The accessibility scan's Playwright config keeps the default output folder, `shell/test-results`, which Playwright empties first, so a scan run after `make e2e` deletes the e2e screenshots in `test-results/e2e`. Give the scan a folder of its own, as the e2e config has.
