# ADR-0044: Naming what a test proves, the traceability matrix, and a check sequence that holds under load

**Status:** Accepted
**Date:** 2026-10-21
**Decided by:** Product owner and architect, at the review of S3 T1

## Context

TF-218 asks that requirement identifiers be traceable to automated tests and their results per release, for the validation that 21 CFR Part 11 and EU Annex 11 tenants must do; since the PRD split it asks the same of every document's identifiers, by identifier and criterion (`REQ-012 AC2`, ADR-0041). Until S3 a test said what it proved only in its name, and the S1 and S2 plans said in prose which tests stood for which requirement. Nothing showed which criteria of a done slice no test proves.

`docs/prompts/S3.md` section T1 asks every test to name what it proves, in a form per kind of test, with the build refusing a test without one; a matrix built from the requirement documents and the tests' result files; an exceptions list for what a done slice left unproven; the matrix in CI; and a check sequence that holds when the shell's tests run straight after `make test` with the stack up, where page tests had timed out at Vitest's five seconds (STATUS, L2).

## Decision

### 1. What a test names

- **A requirement's criterion**, `TF-033 AC2` (or `REQ-012 AC1` for a suite's): the test sets up the criterion's Given, does its When and checks its Then. A unit test counts where the unit decides the outcome.
- **A whole requirement**, `TF-033`, only when the test proves every criterion of it.
- **A decision**, `ADR-0029`, when the test proves a rule an ADR decided that no criterion states: a rule of the build or the architecture, or a mechanism's behaviour.
- A test names as many as it proves. A test that proves nothing a requirement or an ADR states is marked **untraced**, with its reason. It is listed for the design session, which writes the requirement or the decision it proves or removes the test, and it is never tagged to fit.

The grammar is one regular expression, held in the analyzer, the shell's lint rule and the trace: `[A-Z]{2,3}-[0-9]{3}( AC[1-9][0-9]*)?` or `ADR-[0-9]{4}`. Whether an identifier exists is the trace's question, because only it reads the documents.

### 2. The form, by kind

| Kind | Form | Refused by |
|---|---|---|
| .NET | `[Proves("TF-033 AC2", "ADR-0030")]` below `[Fact]` or `[Theory]`, from `Iron.Testing`, usable several times; `[Untraced("why")]` alone | the analyzer `tests/Iron.Testing.Analyzers`: IRON0005 (a test method without either), IRON0006 (an identifier out of form), IRON0007 (`Untraced` beside `Proves`) |
| Vitest and Playwright | the title ends with the list: `"... [TF-033 AC2, ADR-0029]"`; `[untraced]` alone, with `// Untraced: why` on the line above | ESLint's `iron/test-proves` on `src/**/*.test.{ts,tsx}` and `e2e/**/*.spec.ts`: no list, an identifier out of form, `untraced` beside another, a title that is not a literal |
| The smoke | `report.StepAsync("login", ["TF-011 AC1"], ...)`; the step's report line and the JSON report carry the list | the trace (a step without a list) |
| Python tools' tests | the docstring ends with the list | the trace |

`[Proves]` is also an xUnit trait named `proves`, with the requirement alone beside each criterion, so `dotnet test --filter "proves=TF-033"` runs every test of TF-033 and `--filter "proves=TF-033 AC2"` those of the criterion. `Directory.Build.props` puts the analyzer into every test project (layer Test, output Exe), so no test project can leave it out, and the refusal is at the method, in the IDE as in the build. The analyzer project is the one Test project named `*.Testing.Analyzers`, which the layer rule accepts.

### 3. The matrix: `make trace`

`tools/trace.py`, Python's standard library only like `tools/prd_check.py`, whose parser it shares.

- **The tests and what they prove are read from the sources**: the C# test methods with their attributes, the `it` and `test` calls of the shell (with comments, strings and templates masked, so a fixture's source in a string is not a test), the smoke's steps, and the Python tests' docstrings. A test that did not run (the browser tests and the smoke need the stack) is still in the matrix and still proves what it names.
- **The results are read from the last run's files** under `artifacts/test-results` (not committed): TRX from `make test` (`--report-xunit-trx`), JUnit from Vitest, from the browser tests and from the scan, the smoke's report, and JUnit from the Python tools' tests (`tools/unittest_junit.py`). xUnit's TRX names the class and method of each result but not its traits, so the source is where the identifiers come from. A title with placeholders (`${scheme}`) matches each filled-in result, and a test with several results has failed when any failed.
- **It writes** `artifacts/trace/matrix.md` to read and `matrix.json` for the validation pack (T8): the summary, each done slice's criteria with their tests and results or their exception, every requirement a test names, every decision a test names, the untraced tests with their reasons, and the tests without a result although their kind ran.
- **It fails**, naming each problem, on an identifier no document holds (a prefix, a requirement, a criterion number or an ADR), a test that names nothing, a done slice's criterion without a test unless the exceptions list it, an exception that is out of form, outside a done slice or proven by a test now, and a result it cannot place on a test it read, so a gap in its reading cannot hide.
- **A done slice** is a plan in `docs/slices/` whose task table's every State starts with "done"; its requirements are the first column of its coverage table, short forms included (`TF-040 to 043, 045, 046`).
- **The exceptions** are `docs/trace/exceptions.md`: one row per criterion, or a whole requirement, with the reason and the date the design session approved it. T1 writes the first list from the backfill; the review approves it.

### 4. CI and the release

Each CI job that runs tests uploads its result files; a `trace` job after them, whatever they answered, runs `make trace` and uploads `artifacts/trace` as `traceability-matrix` on every run. The browser tests and the smoke need the stack and show as not run there. The release workflow's integration stage runs the smoke and then the browser tests (`make e2e`) against the tenant it provisions, and tags the images `integration` only when both pass (added at T1's review). Its `trace` job waits for CI's run of the same commit, takes its result files with the stage's smoke report and browser test results, builds the matrix and keeps it for 90 days as `traceability-matrix-<commit>` with the run that built and tagged the images. T8's validation pack starts from it.

### 5. A check sequence that holds under load

Measured with the stack running (four tenant units, the control plane and the telemetry stack; WSL with 11.7 GB and 24 threads), Vitest straight after `make test`, then alone:

| Run | Workers | Available at start | Least free memory | Swapped out | Slowest page test | `lint.test.ts` | Gallery test |
|---|---|---|---|---|---|---|---|
| After `make test` | 8 | 5.0 GB | 147 MB | 84 MB | 2.4 s | 11.1 s | 5.2 s |
| After `make test` with a rebuild, then lint | 8 | 4.8 GB | 88 MB | 10 MB | 1.8 s | 5.7 s | 4.5 s |
| After `make test` | 6 | 4.2 GB | 100 MB | 26 MB | 2.1 s | 9.3 s | 6.2 s |
| After `make test` | 4 | 5.6 GB | 700 MB | none | 0.75 s | 4.8 s | 2.3 s |
| Alone, 90 s later | 8 | | 97 MB | none | 1.1 s | 6.3 s | 3.6 s |

The processor was never the limit: it stayed 17 to 60 per cent idle. Memory was. Each worker holds a jsdom and a React tree, about 360 MB at its peak. When the workers took free memory under about 150 MB beside the stack, the kernel reclaimed and swapped, and page tests ran two to six times slower than alone. The tests that pass under load at all do so because earlier tasks raised their own limits (the lint tests to 60 s, the gallery to 20 s, some pages' waits to 10 s). Four workers with 5.6 GB available swapped nothing and ran as fast as alone.

- **The choice: fewer workers when memory is short.** `vite.config.ts` gives Vitest one worker per 1.25 GiB of available memory (`os.freemem()`, which is MemAvailable on Linux), at least 2, and at most half the cores and 8 as before. On a free laptop that is still 6 to 8; beside the stack after `make test`, 3 or 4; on CI's runner, 2 by its cores.
- **`make verify`** runs `make prd-check`, `make test`, `make shell-check`, `make shell-a11y`, `make format-check` and `make trace`, in that order, each to its end before the next, stopping at the first failure. It is the check before a push with the stack running.

## Options considered

- Read the identifiers from xUnit's XML report, which carries traits: rejected, a test that did not run would vanish from the matrix, and the browser tests and the smoke would need another route anyway.
- An architecture test that reads the test assemblies for the attribute: rejected, it needs every test project built first and refuses late; the analyzer refuses at the method.
- A separate list of tests and the requirements they prove: rejected, it drifts from the tests it describes.
- A longer default limit for the page tests only: rejected, the slowdown came from swapping, which has no bound, and a longer limit would hide it; the tests already carrying one show how the limit creeps.
- A pause between the suites: rejected, the stack holds the memory whether or not `make test` just ran, and the run after a pause still swapped beside it.
- Fewer workers always (four): rejected, a free machine would run the suite slower than it needs to, and a smaller laptop could still swap with four.
- The matrix in the shell's Node, or as a .NET tool: rejected, it reads markdown, XML and JSON, which Python's standard library does in one file with nothing to install, beside `prd_check.py`.

## Consequences

- Easier: every criterion shows the tests that prove it and their last results; a done slice's gap is a failure or a listed, approved exception; a developer runs the tests of one requirement by its identifier; T8's validation pack has its source.
- Easier: the shell's tests run beside the stack without swapping, so `make verify` holds on the laptop.
- Harder: a test's identifiers are a claim the review reads like code; a title is longer; a theory names what all its rows prove together.
- Harder: an untraced test stays visible until the design session decides it.
- Revisit: when T8 builds the validation pack from `matrix.json`; when a suite's first tests name its prefixes (the trace reads them from `tools/prd_check.py`'s documents); if the trace's reading of the sources ever misses a test, which the unplaced result names.
