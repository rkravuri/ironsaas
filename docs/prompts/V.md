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
