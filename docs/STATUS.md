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
| A7 Delegation and access reviews | built, waiting for review | slice/s2-a7 | ADR-0037; six commits after the prompt (1726056), 80094ad to the status commit; `make up-control` now migrates and seeds the control plane before it starts; the control plane, acme, bravo and echo carry A7, smoke 11 of 11 and e2e 19 of 19 on echo, with the delegation and review path |
| A8 Cross-organisation access and lifecycle | | | |
| A9 Explanation and segregation of duties | | | |
| S2-close | | | |

## Open questions

- A7: may a reviewer decide an item about their own role? The section says a reviewer decides the items of their units, so today they may; a segregation-of-duties rule would refuse it and leave such an item to another reviewer of the unit, or to escalation when there is none. A9 (segregation of duties) is the natural home if the answer is no.

## Later items

- Before S2 closes: each e2e run creates its own child unit named after the run and checks only inside it, so leftovers never affect a later run. Today every run adds invited accounts to echo's one `E2E branch`, and a check that lists the unit must accept every kind a test ever left there.
- Before S2 closes: the shell test suite passes reliably on a loaded machine. Find the slow tests, bound the workers, and keep explicit timeouts only where a test renders something heavy; today a test can time out at 5 seconds when `make shell-check` runs right after `make test`, or beside the running stack: at A7's end the login page, role import and invite dialog tests timed out in `make shell-check`, and all 200 passed bounded to 8 workers.
- Dependabot's first pull requests bump actions to new majors; their CI runs are red until the pins are checked one by one.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
- A password reset flow (S3) is also what an operator needs when a tenant's administrator password is lost.
- Units cannot be reactivated, merged or split yet (TF-223, A8).
- The Users grid shows type and status in words and has no filter on them; a filter that offers the words (a choice filter in `DataGrid`) is a later item.
- The last-administrator guard counts tenant-wide assignments, not active principals (ADR-0031): a disabled last administrator still counts.
- No kernel module declares a business record (A3 step 6), so no module registers a record policy yet; the first business or products module brings the first, and the architecture test then reads its `AddServices`.
- Record policies guard paging and reads by id (ADR-0032). A module's own Entity Framework reads for background work are not checked by the build; they must not reach a response.
- A record-level refusal (403 or 404 from a policy) is logged, not audited; the audit of reads arrives with A8.
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
- Since A7 the console's navigation and its opener show Delegations to everyone, since anyone may delegate; the console's administration pages stay with their permissions.
- Since A6 the density follows the person, so the Users grid's browser test starts from comfortable and removes the administrator's density at the end; a test that changes a preference cleans it up the same way.
