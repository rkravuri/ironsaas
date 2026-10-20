# IronSaaS Foundation: standing rules for Claude Code

IronSaaS Foundation is the enterprise SaaS platform for regulated industries, and the eProcurement Suite is the first suite built on it (namespace `Iron.*`, ADR-0043). Read this file first, then `.claude/rules/backend.md` and `.claude/rules/frontend.md` for the side you touch, then `docs/STATUS.md`. The task is a section of `docs/prompts/<slice>.md`; its opening message is what you were given.

## How a task runs

1. Create the branch named in the opening message (`slice/<task>`) from `main`, and commit the prompt file alone first (`docs/prompts/<slice>.md` as it stands), so the intent is in history before the work. If any slice/* branch is not merged into main, stop and say so before branching; tasks are never stacked.
2. Read what the opening message lists, then the section fully, then the code you will touch.
3. Write a short plan into the session (ten lines at most: order of work, decisions you take, what you will leave out) and build. Do not wait for approval unless the "When to stop" rule applies.
4. Commit as you go, in steps that build and test, with messages that say why.
5. Keep `docs/STATUS.md` current: what is done, what is next, open questions, later items.
6. Finish with the checks the section names, `make format`, and a report: what you built, what you left out and why, the test counts, the answers to the brief's six questions for every page you touched (ADR-0024, `docs/design/design-language.md` section 9), and screenshots of every page in both colour schemes (Playwright, saved under `shell/test-results/`). Then stop. Never merge; the design session answers with the merge or with corrections.

## When to stop

Stop and ask, in the session, only when: a decision with real alternatives has no ADR; an action would destroy data, a branch or a secret; a test does not pass after two honest attempts; you need access you do not have; the work needed is outside the section's scope. Otherwise decide, record the decision (an ADR if it is one, a line in STATUS if it is small), and go on.

## Commands

- `.NET`: `make build`, `make test` (the data tests start PostgreSQL, Azurite and OpenBao with Testcontainers, so Docker must run), `make check`, `make format` (CI verifies).
- Shell: `make shell-install` once, `make shell-check` (lint, type check, tests, build), `make shell-a11y` (the gallery scan), `make shell-dev`.
- Everything in order: `make verify` runs `make prd-check`, `make test`, `make shell-check`, `make shell-a11y`, `make format-check` and `make trace` (the traceability matrix into `artifacts/trace`, ADR-0044), each on its own.
- Requirements: `make prd-check` (CI runs it); after editing a requirements document, `make prd-csv` writes its CSV.
- Migrations: `make ef-add MODULE=<Module> NAME=<WhatChanged>` after an entity change (`MODULE` is the last segment of the module's name: `Identity` for `Iron.Kernel.Identity`); commit the generated files; never edit a committed migration.
- The stack: `make up`, `make images`, `make provision TENANT=x`, `make update-unit TENANT=x`, `make smoke`, `make e2e`, `make integration`. The runbook is `docs/guides/local-stack.md`.
- Work in WSL: every git, dotnet, npm and docker command runs inside the Linux file system, never through a Windows path.

## Rules the build enforces (do not fight them)

- Warnings are errors; no suppression without a comment that says why.
- Package versions live once: `Directory.Packages.props` and `shell/package.json`. Exact pins, at least two weeks old (ADR-0003).
- Module boundaries (ADR-0001); time through `TimeProvider`, ids through `Ids.New()`, failures through `Result` (ADR-0007); every endpoint declares its permission; state changes write their audit event in the same transaction (ADR-0013); in the shell, no `ag-grid-*` outside the wrapper, every route declares its permission, every string in the catalogue, no colour by value (ADR-0024).
- Every test names the requirement criteria or the decision it proves, read against the criterion's text: `[Proves("TF-033 AC2")]` in .NET, a title ending `[TF-033 AC2]` in Vitest and Playwright, the step's list in the smoke, or untraced with its reason, never tagged to fit (ADR-0044). `make trace` fails on an identifier that does not exist and on a done slice's criterion without a test or an approved exception.

## Layers (ADR-0029, ADR-0043)

Code lives in one of three layers: Kernel (`Iron.Kernel`, `Iron.Kernel.*`) and Business (`Iron.Business.*`), which together are IronSaaS Foundation, and Suite (`Iron.Suites.<Suite>.*`, one folder per suite: `src/Suites/Procurement/` for the eProcurement Suite). Before creating a module, apply the placement test: no business meaning is Kernel; needed by enterprise products of any kind, or by the products of two or more suites, is Business; needed by two or more products of one suite is that suite's shared master; owned by one product is that product. Every project declares `<IronLayer>`, a suite's project `<IronSuite>` too. Dependencies go downward only, and between modules only through a Contracts project; a suite never references another suite, and the Foundation never references a suite. When the kernel needs something only Business can supply, the kernel declares an interface in its Contracts and Business implements it. Schemas carry the layer prefix or the suite's short name: `kernel_`, `business_`, `procurement_`. Permission names, audit events, error codes, routes and configuration sections name the module, not the layer or the suite.

## Writing rules

- Plain English, short sentences. No em dash character anywhere, in code comments, docs, commit messages or UI text; use a comma, a colon or a full stop.
- Comments say why, not what. Names are words, not abbreviations.
- UI text: second person, no exclamation marks, errors say what happened and what to do.

## Documentation

The requirements are in `docs/prd/`, one document per layer and product, the Foundation's in `foundation/` and each suite's in `suites/<suite>/` (ADR-0041, ADR-0043, its README): cite a requirement by its identifier, and a test names what it proves by identifier and criterion (`REQ-012 AC2`). Requirements depend down the layers and along the process order between a suite's products (ADR-0042), never from one suite to another. A decision not already in an ADR gets one (`docs/adr/0000-template.md`, then the index). Behaviour that changed is in the developer guide, the runbook or the module README. Every font, image or dataset has its licence in `docs/licences.md`. The definition of done is `docs/guides/definition-of-done.md`.
