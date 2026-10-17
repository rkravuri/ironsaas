# Developer guide

This guide grows with each slice. Today it covers how to work on the repository, the rules the build enforces, and how to add a module. Later tasks add: the module pattern (T7), adding an entity with endpoints, permissions and migrations (T7 to T11), the local stack (T2), and the shell (T15).

## Working on the repository

- Develop in WSL 2 (Ubuntu) or on Linux. Clone inside the Linux file system (for example `~/src/foundation`), not under `/mnt/c`; builds are many times faster there.
- Install the .NET SDK 10.0. `dotnet --version` must print 10.0.x. `global.json` accepts any 10.0.1xx feature band.
- Commands: `make build`, `make test`, `make check`, `make format`. `make` alone lists them.
- Before you push: `make test` is green and `make format` made no change.
- Every pull request follows the checklist in `.github/pull_request_template.md`.

## Layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project
Directory.Packages.props     every package version, once, with its licence
global.json                  SDK band and the test runner
Makefile                     the developer commands
src/
  Iron.Core/  Iron.Data/  Iron.Api/  Iron.Telemetry/  Iron.Security/    shared libraries (no schema)
  Iron.<Module>/             one folder per module
    Api/                     endpoints (thin)
    Services/                business logic (plain classes)
    Data/                    entities, repositories, migrations for this module's schema
    Contracts/               public interfaces and DTOs; its own project Iron.<Module>.Contracts
    README.md                what the module is responsible for
  Iron.Host.Api/  Iron.Host.Worker/              tenant unit hosts
  Iron.Host.ControlPlane/  Iron.Host.Fleet/      control plane hosts
  Iron.Shell/                React shell and design system (T15)
products/                    business products, same layout as a module (after S2)
tests/                       test projects; Iron.Architecture.Tests holds the build checks
tools/                       scaffolding and developer tools (after S1)
infra/                       Compose stack (T2), later Kubernetes and OpenTofu
docs/                        prd/, adr/, guides/, slices/
```

## Rules the build enforces

| Rule | When it fails | What to do |
|---|---|---|
| Warnings are errors | Any compiler or analyzer warning | Fix the warning. Do not suppress it without a comment that says why. |
| One version per package, in `Directory.Packages.props` | A project has a `Version` attribute on a `PackageReference`, or a package is missing from the props file | Add the package to `Directory.Packages.props` with its licence; reference it by name only. |
| Module boundaries (ADR-0001) | A module references another module's implementation project; a Contracts project references anything but `Iron.Core`; a shared library references a module; a host references another host | Move the shared type into the other module's `Contracts` project and reference that. |
| Private namespaces (ADR-0001) | An assembly uses a type from another module's `Api`, `Services` or `Data` namespace | Expose what you need through the other module's `Contracts` project. |
| Layout | A project is not in a folder with its own name; a Contracts project is outside its module; a module has no Contracts project; a project under `src/` does not start with `Iron.` | Move or rename the project; add the missing Contracts project. |

The messages name the project file or the assembly and the forbidden reference.

## Add a module

Example: a new module `Iron.Settings`.

1. Create `src/Iron.Settings/` with the folders `Api/`, `Services/`, `Data/` and `Contracts/`.
2. Copy `src/Iron.Audit/Iron.Audit.csproj` to `src/Iron.Settings/Iron.Settings.csproj`. Change the Contracts reference to `Contracts/Iron.Settings.Contracts.csproj`. Keep the `DefaultItemExcludes` line; it keeps the Contracts sources out of this project.
3. Copy `src/Iron.Audit/Contracts/Iron.Audit.Contracts.csproj` to `src/Iron.Settings/Contracts/Iron.Settings.Contracts.csproj`.
4. Add both projects to `Foundation.slnx` under a new folder `/src/modules/Iron.Settings/`.
5. Add both projects to the references of `tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj`, so the private-namespace check can read their assemblies. The test tells you when you forget.
6. Reference the module from the hosts that run it (`src/Iron.Host.*`).
7. Write `src/Iron.Settings/README.md`: one paragraph on the module's responsibility, taken from PRD section 2.1, and its schema name.
8. Run `make check`. It confirms the layout and the references.
9. Then follow the module pattern (from T7): the module class, its permissions, its DbContext and migrations, its endpoints, its tests.

## Add a product

Same steps under `products/<Name>/`, with namespace `<Name>` (for example `Procurement`) and schema `procurement`. The same rules apply; the architecture tests treat a product like a module.

## Write an ADR

Copy `docs/adr/0000-template.md` to the next number, fill it in, and add it to the table in `docs/adr/README.md`. A pull request that introduces a decision without its ADR is not merged (TF-209).
