# ADR-0040: Prettier formats the shell

**Status:** Accepted
**Date:** 2026-10-20
**Decided by:** Product owner and architect

## Context

The .NET side has had its formatter from the start: `dotnet format` from `.editorconfig`, run by `make format` and verified in CI (ADR-0004). The shell had none. Its code was written to a long line by hand, so each author's wrapping differed, and a bare `npx prettier` run in A9 reflowed files nobody had touched to Prettier's default width of 80. S2-close asked for a formatter that `make format` runs and CI checks, with the house settings.

## Decision

1. **Prettier formats the shell's code**: TypeScript, TSX, JavaScript, CSS, JSON and HTML under `shell/`, except build output, reports, the lock file and the README (`shell/.prettierignore`). It is a dev dependency pinned exactly (3.9.9, ADR-0003).
2. **The settings**, in `shell/.prettierrc.json`: a line width of 120, for readability in diffs and side-by-side review, where two files of 120 characters fit beside each other on a screen and a changed line is seen whole; double quotes; trailing commas wherever the language allows them; parentheses round every arrow function's parameters. The quotes, commas and parentheses are the code's own: over its 255 files, Prettier's defaults for them differed least from the code as written. The width was decided in review: S2-close first measured the width the code was written to (200, which 99.7 per cent of its 19,097 lines fitted) and formatted at it.
3. **A formatting commit formats the shell and nothing else**, so `git blame` can skip it: S2-close's fifth commit at 200, and the commit after the review at 120.
4. **`make format` runs `dotnet format` and then Prettier; `npm run check` runs Prettier's check** after lint, so `make shell-check` and CI's shell job refuse unformatted code, as CI's .NET job refuses what `dotnet format` would change.

## Options considered

- The width the code was written to, 200. Rejected in review: a 200-character line does not fit beside another in a side-by-side diff, and a change inside it is hard to see.
- Prettier's default width of 80. Rejected: it splits the shell's JSX and its long calls into many short lines, more than readability in review needs.
- ESLint's stylistic rules instead of Prettier. Rejected: more rules to keep, and no single command that rewrites a file the way the check wants it.
- Biome. Rejected for now: a second toolchain beside ESLint, which the shell's own rules (no AG Grid outside the wrapper, no colour by value, the layer imports) need.

## Consequences

- Easier: formatting is never a review comment, an editor that runs Prettier on save writes what the check expects, and a diff reads side by side.
- Harder: the code written to 200 wraps more, so files are longer; a Prettier upgrade can move lines, and it goes through Dependabot with the rest, a version that reformats landing as its own commit.
