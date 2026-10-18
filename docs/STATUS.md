# Status

The running state of the current slice, kept current by Claude Code at the end of every session. The slice plan is `docs/slices/S2-access-control-organisations-and-design.md`; the tasks are in `docs/prompts/S2.md`.

## Slice S2

| Task | State | Branch | Notes |
|---|---|---|---|
| D1 Design foundation | done | main (f145e5f) | accepted 2026-10-18; six commits plus the State column |
| D2 The drawn world | in progress | slice/s2-d2 | branched from e1c52db on 2026-10-18 |
| D3 Templates, panels and forms | | | |
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
- OpenBao in development mode loses every secret when it restarts.
