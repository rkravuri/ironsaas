# Status

The running state of the current slice, kept current by Claude Code at the end of every session. The slice plan is `docs/slices/S2-access-control-organisations-and-design.md`; the tasks are in `docs/prompts/S2.md`.

## Slice S2

| Task | State | Branch | Notes |
|---|---|---|---|
| D1 Design foundation | done | main (f145e5f) | accepted 2026-10-18; six commits plus the State column |
| D2 The drawn world | done | main | merged 2026-10-18; six commits (800a1c8 to 560e9b8) plus the figure correction from the review |
| D3 Templates, panels and forms | done, pending merge | slice/s2-d3 | built 2026-10-18 in five commits; the report, the diff summary and the pictures in shell/test-results go to the design session |
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

- The keyboard test against the smoke tenant (shell/e2e/users.spec.ts) is written but was not run in the D3 session: the control plane was not running, so make update-unit could not deploy the rebuilt shell image (foundation/iron-shell:dev now holds the D3 build). To run it: make up-control, make update-unit TENANT=smoke, make e2e.
- Saved views on the grid (S5); the approval and comment panels get their behaviour with the workflow and comments slices; an attachment panel on a real record wires the storage API of its module.
- The Users page opens a dialog from the row's own data; once the Identity API reads a user by id, it becomes a user record page on RecordPage.
- The grid-import lint test (src/lint.test.ts, the first case) failed once in a full make shell-check on main run straight after make test, and passed alone and in the next full run; its first case takes 5 to 9 seconds of type-checked linting under a 60-second limit. If it recurs, give the lint tests their own worker or a longer limit.
- Tenant colours (TF-173) replace the primary accent in the mark and in the drawings' tints; the generator and the Drawing frame already take an accent.
- Dividers and section markers from the generator (brief section 4) land with the templates (D3); the home page uses the Home drawing (D4).
- A drawing a product adds follows docs/design/illustration-checklist.md; the gallery tile and the illustrations test are the proof.
- Dependabot's first pull requests bump actions to new majors; their CI runs are red until the pins are checked one by one.
- The chiseled images print a Kerberos probe message from Npgsql at start (cosmetic).
- A password reset flow (S3) is also what an operator needs when a tenant's administrator password is lost.
- The shell does not warn before the session expires (TF-021, D4).
- OpenBao in development mode loses every secret when it restarts.
