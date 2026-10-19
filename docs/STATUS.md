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
| A4 Entitlements | next | | docs/prompts/S2.md section A4 |
| A5 Feature flags | | | |
| A6 Settings | | | |
| A7 Delegation and access reviews | | | |
| A8 Cross-organisation access and lifecycle | | | |
| A9 Explanation and segregation of duties | | | |
| S2-close | | | |

## Open questions

(none)

## Later items

- Dependabot's first pull requests bump actions to new majors; their CI runs are red until the pins are checked one by one.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
- A password reset flow (S3) is also what an operator needs when a tenant's administrator password is lost.
- Units cannot be reactivated, merged or split yet (TF-223, A8).
- The Users grid shows type and status in words and has no filter on them; a filter that offers the words (a choice filter in `DataGrid`) is a later item.
- The last-administrator guard counts tenant-wide assignments, not active principals (ADR-0031): a disabled last administrator still counts.
- No kernel module declares a business record (A3 step 6), so no module registers a record policy yet; the first business or products module brings the first, and the architecture test then reads its `AddServices`.
- Record policies guard paging and reads by id (ADR-0032). A module's own Entity Framework reads for background work are not checked by the build; they must not reach a response.
- A record-level refusal (403 or 404 from a policy) is logged, not audited; the audit of reads arrives with A8.
- The control plane still runs the image from A2 (8 of 9): A3 and D4 change nothing it serves, and the next `make up-control` brings it up to date.
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
