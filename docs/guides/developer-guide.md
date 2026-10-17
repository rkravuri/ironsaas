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

## Writing code: the Core conventions (ADR-0007)

- A service returns `Result` or `Result<T>` for expected failures and never throws for them. `return Error.NotFound("identity.user.not_found", "The user does not exist.");` reads as plain code. Exceptions are for programmer errors and infrastructure failures.
- Error codes are lowercase dotted segments, at least two: `module.resource.reason`. The constructor rejects other shapes.
- Time: inject `TimeProvider` and call `GetUtcNow()`. Never `DateTime.UtcNow`; the build fails on it. Store UTC.
- Ids: `Ids.New()` (UUID version 7). Never `Guid.NewGuid()` for a key.
- Argument checks: `ArgumentNullException.ThrowIfNull(x)`, `ArgumentException.ThrowIfNullOrWhiteSpace(s)`, `ArgumentOutOfRangeException.ThrowIfNegative(n)`.
- The tenant is `TenantContext`, a singleton bound at start. Never pass a tenant id through business code; the unit serves one tenant.
- Lists take a `ListQuery` and return a `Page<T>`.

## Writing an entity and a module context (ADR-0008)

- Pick the base class: `BusinessRecord` for business and master data (it needs an owning organisation), `AuditedEntity` for platform data that changes, `AppendOnlyEntity` for rows that are never changed or deleted. Add `ISoftDeletable` when a record may be deleted by users.
- Give the entity a private parameterless constructor for Entity Framework and public constructors for your code. Change state through methods, not public setters.
- Never set `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy` or `Version`. The save interceptor fills them from `TimeProvider` and `ICurrentPrincipal`.
- The module's context derives from `ModuleDbContext`, carries `[Schema("module")]`, exposes a `DbSet<T>` per entity and maps module-internal relations and indexes in `ConfigureModule`. Snake_case names, the key, the version token, the soft-delete filter and the append-only comment come from the conventions.
- Never add a foreign key to a table of another module. Store the other record's id as a `Guid`.
- Register the context in the host with `services.AddIronDbContext<MyDbContext>()` after `services.AddIronDatabase(connectionString)`.
- Tests that touch the database use the `PostgresFixture` pattern from `tests/Iron.Data.Tests`: a throw-away container, `EnsureCreated` from the context, `FakeTimeProvider` and a fixed principal.

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
