# Foundation

Foundation is the technical base for a family of enterprise SaaS products by IronSaaS. Product teams build business modules on it and get multi-tenancy, identity, permissions, audit, telemetry, a UI shell and the operations tooling without writing any of it themselves.

Code namespace: `Iron.*`. Database schemas carry the module names.

The requirements live in `docs/prd/`. The build plan is in slices (PRD section 7). Slice S1, the walking skeleton, is in progress: see `docs/slices/S1-walking-skeleton.md`.

## Status

| Task | State |
|---|---|
| T1 Repository skeleton and build checks | done |
| T2 Platform stack with Compose | next |

## Prerequisites

- Linux, or WSL 2 with Ubuntu. The runtime is Linux; nothing targets Windows.
- .NET SDK 10.0. `global.json` accepts any 10.0.1xx feature band.
- GNU make and git.
- From T2: Docker Engine with the Compose plugin.
- From T15: Node.js v26.12.0 (see `.node-version`).

Check: `dotnet --version` prints 10.0.x.

## Quick start

```bash
make build     # build every project; warnings are errors
make test      # run every test project
make check     # run only the architecture checks
```

`make` with no target lists the commands.

## Repository layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project
Directory.Packages.props     every package version, once, with its licence
global.json                  SDK band and the test runner
Makefile                     the developer commands
src/
  Iron.Core/  Iron.Data/  Iron.Api/  Iron.Telemetry/  Iron.Security/    shared libraries
  Iron.Authorization/  Iron.Audit/  Iron.Identity/  Iron.Files/        tenant modules
  Iron.Tenancy/  Iron.Fleet/                                            control plane modules
    Api/  Services/  Data/  Contracts/  README.md                       inside every module
  Iron.Host.Api/  Iron.Host.Worker/              tenant unit hosts
  Iron.Host.ControlPlane/  Iron.Host.Fleet/      control plane hosts
  Iron.Shell/                React shell and design system (T15)
tests/                       Iron.Architecture.Tests holds the build checks
products/  tools/  infra/    products, developer tools, the laptop stack (later tasks)
docs/                        prd/, adr/, guides/, slices/
```

## What the build enforces today

- Warnings are errors in every project.
- Every package version is declared once in `Directory.Packages.props`, with its licence.
- Module boundaries: a module may reference shared libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation.
- Another module's `Api`, `Services` and `Data` namespaces are private. The architecture tests read the compiled assemblies and fail on any type reference into them.
- Every project sits in a folder with its own name; every module has a `Contracts` project inside it.

## Try the boundary check

1. Open `src/Iron.Audit/Iron.Audit.csproj`.
2. Add `<ProjectReference Include="../Iron.Identity/Iron.Identity.csproj" />` to the item group.
3. Run `make check`. The test `EveryProjectReferenceFollowsTheModuleBoundaryRules` fails and names `src/Iron.Audit/Iron.Audit.csproj`.
4. Remove the line again.

## Documentation

- `docs/slices/` the slice plans; S1 is the current one and the brief for every build session
- `docs/adr/` architecture decision records; every decision gets one as it is built
- `docs/guides/developer-guide.md` how to work on the repository and add a module
- `docs/prd/` the Technical Foundation PRD and the Functional Foundation PRD

## Versions decided for Foundation

| Component | Version |
|---|---|
| .NET | 10 (LTS) |
| PostgreSQL | 19 |
| Node.js | v26.12.0 |
| React | 19.3 |
| Mantine | 9.7.3 |
| AG Grid | Community edition; Enterprise later by decision |
