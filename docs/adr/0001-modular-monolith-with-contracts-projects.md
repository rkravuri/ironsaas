# ADR-0001: Modular monolith with one Contracts project per module

**Status:** Accepted
**Date:** 2026-10-17
**Decided by:** Product owner and architect

## Context

PRD principles P1 (simplicity first) and P2 (modular monolith) ask for plain layers inside a module and clear public interfaces between modules. Section 3.5 gives the folder layout: one folder per module with `Api`, `Services`, `Data` and `Contracts`, and the rule that a module never references another module's `Data` folder. The rule must be enforced by the build, not by code review.

## Decision

- One repository. One folder per module under `src/`, named `Iron.<Module>`. Business products use the same layout under `products/`.
- Inside a module: `Api/` (thin endpoints), `Services/` (plain classes), `Data/` (entities, repositories and migrations for the module's own schema) and `Contracts/`.
- `Contracts/` is its own project, `Iron.<Module>.Contracts`. It holds the interfaces and DTOs other modules may use. It references `Iron.Core` only. Another module is referenced by identifier, never by type.
- A module project references: the shared libraries (`Iron.Core`, `Iron.Data`, `Iron.Api`, `Iron.Telemetry`, `Iron.Security`), its own Contracts project, and other modules' Contracts projects. Never another module's implementation project.
- A host project (`Iron.Host.*`) references shared libraries, modules and Contracts projects, and lists its modules explicitly in `Program.cs`. No assembly scanning.
- Public surface of a module, as seen by other assemblies: its `Contracts` namespace, and its root namespace (the module class the hosts call). Its `Api`, `Services` and `Data` namespaces are private.
- Enforcement in two layers. First the compiler: a module cannot see another module's types because it never references that project. Second `tests/Iron.Architecture.Tests`: it reads every project file and every compiled assembly and names each violation. Both run in `make test` and in CI.
- Because the Contracts project sits inside the module folder, every module project excludes `Contracts/**` from its own compile items (`DefaultItemExcludes`). This line is part of the module template.

## Options considered

- Contracts as a folder inside the module project, enforced only by a test. Rejected: the compiler would allow the violation and the test would catch it later.
- Contracts as a sibling folder `src/Iron.<Module>.Contracts/`. Rejected: breaks the "one folder per module" reading of the layout; the nesting costs one line in the project file.
- A Roslyn analyzer package for the boundary rules. Deferred: a plain xUnit test is easier for a new developer to read and change. An analyzer can be added later if the tests become slow.

## Consequences

- Easier: a new developer opens one module folder and reads it end to end; the boundary is visible in the project file; the build output names the file to fix.
- Harder: a cross-module need must go through a Contracts interface, which takes a few more lines than a direct call. This is the point.
- Revisit: when the first product arrives (after S2), confirm the same rules hold for `products/` without change.
