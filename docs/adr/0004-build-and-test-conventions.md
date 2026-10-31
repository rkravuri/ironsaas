# ADR-0004: Build and test conventions

**Status:** Accepted
**Date:** 2026-10-17
**Decided by:** Product owner and architect

## Context

TF-190 asks for enforced conventions as build checks and TF-191 for a pipeline that builds, tests and scans every change. The repository starts empty, so the conventions are set now and every later task inherits them.

## Decision

- Solution file in the XML `.slnx` format (`Foundation.slnx`): readable, no GUIDs, supported by the .NET 10 SDK and by current IDEs.
- `Directory.Build.props` sets the compiler settings for every project: `net10.0`, nullable reference types on, implicit usings on, warnings are errors, .NET analyzers at the latest level, deterministic builds.
- `global.json` pins the SDK feature band (10.0.1xx, latest patch) and selects Microsoft Testing Platform as the `dotnet test` runner.
- Tests use xUnit.net v3 (package `xunit.v3`), which runs on Microsoft Testing Platform. Test projects are executables and can be run directly with `dotnet run`. No `Microsoft.NET.Test.Sdk`, no VSTest adapter. In this mode `dotnet test` takes `--solution` or `--project`.
- Architecture rules are plain xUnit tests in `tests/Iron.Architecture.Tests`. They are the build checks required by TF-190 and run in `make test` and in CI. New rules join the same project as their slices arrive: standard columns and no cross-schema foreign keys (T4), a permission or anonymous marker on every endpoint (T8), owning organisation on every business record (T4), externalised UI strings (S4).
- `Makefile` is the one command surface for developers: `make build`, `make test`, `make check`, `make format`, and later `make up` and `make provision`. Every target calls the real tool, so every command can also be typed by hand.
- CI runs on GitHub Actions (`.github/workflows/ci.yml`): restore, build, test. Security scans, container images and the integration stage arrive in T18.
- Code style lives in `.editorconfig` and is applied with `dotnet format`. Style rules are suggestions today and become build errors once the first modules have code (T7), so that the rule set is tuned on real code first.

**Since V-close (2026-10-31), at the design session's word, two conventions of the build's tooling are checks of their own.** Every shell script the repository tracks (`*.sh`) is executable, mode 100755 in git's index: `ScriptModeRulesTests` in `tests/Iron.Architecture.Tests` reads `git ls-files --stage` and fails naming each script without the bit and the command that gives it back, since editing a script through the Windows path of the WSL file system dropped the bit twice and git committed the new mode with the edit. And the shell's `import.meta.glob` is called without a type argument, its result typed on the variable: Vitest leaves out of its module cache a file whose text holds `import.meta.glob(`, and a type argument hid the call from that check, so a cached transform missed a new module folder (V2). The shell's lint refuses a typed glob (`typedGlobRule` in `shell/eslint.config.js`), and `shell/src/lint.test.ts` proves it.

## Options considered

- Classic `.sln`. Rejected: GUID noise, hard to edit by hand.
- xUnit v2 with VSTest. Rejected: the v3 line is current and v2 is in maintenance.
- A Roslyn analyzer package for the architecture rules. Deferred (see ADR-0001).
- Cake or Nuke build scripts. Rejected: a Makefile is enough and needs no build of the build.

## Consequences

- Easier: one way to build and test on every machine and in CI; a failure names the file to fix.
- Harder: any warning stops the build. This is wanted.
- Revisit: when the test suite grows, run integration tests (containers) in their own CI job (T18).
