# ADR-0040: Prettier formats the shell

**Status:** Accepted
**Date:** 2026-10-20
**Decided by:** Product owner and architect

## Context

The .NET side has had its formatter from the start: `dotnet format` from `.editorconfig`, run by `make format` and verified in CI (ADR-0004). The shell had none. Its code was written to a long line by hand, so each author's wrapping differed, and a bare `npx prettier` run in A9 reflowed files nobody had touched to Prettier's default width of 80. S2-close asked for a formatter that `make format` runs and CI checks, with the house settings.

## Decision

1. **Prettier formats the shell's code**: TypeScript, TSX, JavaScript, CSS, JSON and HTML under `shell/`, except build output, reports, the lock file and the README (`shell/.prettierignore`). It is a dev dependency pinned exactly (3.9.9, ADR-0003).
2. **The settings are the code's own, measured before the first run**, in `shell/.prettierrc.json`: a line width of 200, which 99.7 per cent of the 19,097 lines already fitted; double quotes; trailing commas wherever the language allows them; parentheses round every arrow function's parameters. Over the 255 files, Prettier's defaults for quotes, commas and parentheses differed least from the code, and widths from 160 to 240 were tried.
3. **One commit formatted the shell and nothing else** (S2-close, 5 of the branch), so `git blame` can skip it.
4. **`make format` runs `dotnet format` and then Prettier; `npm run check` runs Prettier's check** after lint, so `make shell-check` and CI's shell job refuse unformatted code, as CI's .NET job refuses what `dotnet format` would change.

## Options considered

- Prettier's default width of 80. Rejected: it rewraps most of the shell and splits the long JSX lines the code was written with into many short ones.
- ESLint's stylistic rules instead of Prettier. Rejected: more rules to keep, and no single command that rewrites a file the way the check wants it.
- Biome. Rejected for now: a second toolchain beside ESLint, which the shell's own rules (no AG Grid outside the wrapper, no colour by value, the layer imports) need.

## Consequences

- Easier: formatting is never a review comment, and an editor that runs Prettier on save writes what the check expects.
- Harder: a Prettier upgrade can move lines; it goes through Dependabot with the rest, and a version that reformats lands as its own commit.
