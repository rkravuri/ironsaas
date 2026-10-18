# Foundation: standing rules for Claude Code

Foundation is the IronSaaS enterprise SaaS platform (namespace `Iron.*`). Read this file first on every task; the task itself is in `docs/prompts/<slice>.md`, one section per task, and the section's prompt is what you were given.

## Commands

- `.NET`: `make build`, `make test` (386+ tests; the data tests start PostgreSQL, Azurite and OpenBao with Testcontainers, so Docker must run), `make check` (architecture tests only), `make format` (run it before you finish; CI verifies).
- Shell: `make shell-install` once, `make shell-check` (lint, type check, tests, build), `make shell-a11y` (the gallery scan), `make shell-dev`.
- Migrations: `make ef-add MODULE=<Module> NAME=<WhatChanged>` after an entity change; commit the generated files; never edit a committed migration.
- The stack: `make up`, `make images`, `make provision TENANT=x`, `make update-unit TENANT=x`, `make smoke`, `make e2e`, `make integration`. The runbook is `docs/guides/local-stack.md`.

## Rules the build enforces (do not fight them)

- Warnings are errors. Do not suppress a warning without a comment that says why.
- Package versions live once in `Directory.Packages.props` (.NET) and `shell/package.json`.
- Module boundaries (ADR-0001): a module references shared libraries and `Contracts` projects only; another module's `Api`, `Services`, `Data` are private. Expose through `Contracts`.
- Time through `TimeProvider`, ids through `Ids.New()`, failures through `Result`/`Error` with dotted error codes (ADR-0007).
- Every endpoint declares `RequirePermission`, `RequireSignedIn` or `AllowAnonymous`; the anonymous list is fixed by a test.
- State changes write their audit event in the same transaction (ADR-0013).
- In the shell: no `ag-grid-*` import outside `src/components/data-grid`; every route declares its permission; every string goes through `src/i18n/en.json`; no colour by value in a page (tokens only, ADR-0024).

## How to work

- Read the task's section fully, then the ADRs it names (`docs/adr/`), then the code you will touch. Follow the existing patterns; the S1 modules are the templates.
- Tests at the level that proves the change: unit for rules, integration on the container fixtures for anything with the database, store or storage, shell tests for pages and mappings, a browser test for a user path. Run them; finish with `make test`, `make shell-check` and `make format`.
- A decision not already in an ADR gets one: copy `docs/adr/0000-template.md`, add it to the index.
- Documentation where behaviour changed: the developer guide, the runbook, the module README, the README's first hour.
- The definition of done is `docs/guides/definition-of-done.md`. Report what you did, what you did not do and why, and the test counts.

## Writing rules

- Plain English, short sentences. No em dash character anywhere, in code comments, docs or UI text; use a comma, a colon or a full stop.
- Comments say why, not what. Names are words, not abbreviations.
- UI text: second person, no exclamation marks, errors say what happened and what to do.

## Design (ADR-0024, `docs/design/design-language.md`)

Calm centre, living edges. The work area is dense and consistent; the art is in the moments. One drawn hand, original SVG only, never a known character, mascot or brand. Colour with a meaning, status never colour alone, WCAG 2.1 AA, keyboard first, reduced motion honoured. Judge every page with the six questions in section 9 of the brief and answer them in the pull request.
