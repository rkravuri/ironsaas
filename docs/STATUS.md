# Status

The running state of the current slice, kept current by Claude Code at the end of every session. The slice plan is `docs/slices/S2-access-control-organisations-and-design.md`; the tasks are in `docs/prompts/S2.md`.

## Slice S2

| Task | State | Branch | Notes |
|---|---|---|---|
| D1 Design foundation | done | main (f145e5f) | accepted 2026-10-18; six commits plus the State column |
| D2 The drawn world | done | main (fd56702) | merged 2026-10-18; six commits plus the figure correction from the review |
| D3 Templates, panels and forms | merged on main, not pushed | slice/s2-d3 (merged) | the scan, format and the keyboard test on the stack passed in the P1 session (2026-10-19, tenant echo); the tables and the push remain |
| P1 The laptop store survives a restart, and an update heals a unit | done, pending merge | slice/s2-p1 | built 2026-10-19; the store initialised and proven across two down/up rounds, acme, bravo and smoke healed, echo provisioned fresh and passing smoke and e2e |
| A1 Organisation units | | | |
| A2 Roles and scoped assignments | | | |
| A3 Record policies | | | |
| D4 Home and the header | | | |
| A4 Entitlements | | | |
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
- The shell does not warn before the session expires (TF-021, D4).
- The administrators' passwords of acme, bravo and smoke went with the old store: those tenants serve their users and their smoke clients (the seed sets a healed account's new secret since P1), but the smoke's administrator steps and the browser tests need echo until the reset flow of S3.
