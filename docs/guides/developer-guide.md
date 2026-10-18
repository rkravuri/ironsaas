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

## Writing an endpoint (ADR-0009)

- Group the resource with `endpoints.MapApiGroup("users")`; the path becomes `/api/v1/users`. Resource names are plural kebab-case nouns.
- Handlers return `IResult`. A service result goes through `ApiResults.From(result, http, value => TypedResults.Ok(value))`; a failure becomes problem details on its own.
- Lists: declare the fields once in a `ListFields<T>` (snake_case names, pointing at the entity properties, with a default sort), parse with `ListQueryParser.Parse(http.Request.Query, fields)`, apply with `.ApplyListQuery(query, fields).ToPageAsync(query.Page, ct)` and return `ApiResults.List(page, http, query)`.
- Request bodies: write an `AbstractValidator<TRequest>`, register it in the module's `AddServices`, and add `.Validate<TRequest>()` to the endpoint.
- Never catch exceptions to return an error status; let the unexpected exception handler produce the 500 problem with the correlation id. Return `Error`s for expected failures.
- The OpenAPI document and the Scalar browser are at `/openapi/v1.json` and `/scalar/v1` in Development.

## Observability and security in a host (ADR-0010)

- `builder.AddIronTelemetry("iron-api", t => t.TenantId = ...)` then `app.UseIronTelemetry()` first in the pipeline. Every request gets a trace, the response carries `X-Correlation-Id`, and metrics and logs flow to the collector. Set `OTEL_EXPORTER_OTLP_ENDPOINT` when the collector is not on `localhost:4317`.
- `builder.Services.AddIronSecurity(builder.Configuration)` then `app.UseIronSecurity()` after `UseIronApi()`. Allowed browser origins go in `Security:Cors:AllowedOrigins`; the shell needs none.
- `builder.Services.AddIronHealth()` plus `app.MapIronHealth()`; a module or infrastructure adds readiness checks with the tag `ready` (`AddIronDatabaseHealthCheck()` for PostgreSQL).
- Log through `ILogger<T>` with structured values (`logger.LogInformation("Tenant {TenantId} provisioned", id)`); never string-concatenate values into the message. Do not log secrets or personal data.

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

## Hosts and unit commands (T7)

A host's `Program.cs` is the only place that decides what runs in a process. It has one shape:

1. `HealthProbe.IsCommand(args)`: answer the `health` command without building the host.
2. `TenantBinding.FromConfiguration`: bind to one tenant or stop.
3. List the modules: `IReadOnlyList<IModule> modules = [...]`.
4. Register the shared libraries, then `AddModules`.
5. Build; if `DatabaseCommands.IsCommand(args)`, run `migrate`, `seed` or `schema-check` and exit.
6. Pipeline order: `UseIronTelemetry`, `UseIronApi`, `UseIronSecurity`, `UseIronTenant`, then endpoints.

Adding a module to a host means adding one line to the list. Adding reference data means one `ISeeder` registered by the module. Adding a table means a migration in the module's project; `migrate` picks it up because the module context is registered with `AddIronDbContext`.

On the laptop: `make run-api` serves http://localhost:5100 bound to tenant `dev` for hostnames `localhost` and `dev.localhost`; `make run-worker` runs the worker on http://localhost:5101 with a heartbeat every 15 seconds; `make migrate` runs the migrate command against the laptop database; `make images` builds the four images.


## An endpoint with a permission (T8)

1. Declare the permission in the module's Contracts project: `public static readonly Permission UsersRead = new("identity.users.read", "List and view users", Risk.Low);` and list it in the module's `Permissions`.
2. Put it on the endpoint or the group: `endpoints.MapApiGroup("users").RequirePermission(IdentityPermissions.UsersRead);`.
3. Run the host. It starts only when every endpoint has a permission or `.AllowAnonymous()`; the message names what is missing.
4. The filter runs before the handler: 401 for nobody, 403 `authz.permission_denied` (with the permission named) for a principal without it, and a denial audit event. OpenAPI shows "Requires permission" on the operation.

Anonymous endpoints are the short list of TF-030: health, login, recovery, invitation acceptance, public branding. `Iron.Host.Tests` keeps the list fixed; adding to it is a reviewed change.

## A table and its migration (T8)

1. Add or change the entity in the module's `Data/` folder (derive from `AuditedEntity`, `AppendOnlyEntity` or `BusinessRecord`) and map it in `ConfigureModule`.
2. Generate the migration: `make ef-add MODULE=Authorization NAME=AddRoleColour`. The first time, `dotnet tool restore` installs `dotnet-ef` from `.config/dotnet-tools.json`. The generated files land in `src/Iron.<Module>/Data/Migrations`; read the `Up` method once, then commit them with the entity change.
3. `make test`: the module's `ModelTests` passes only when the migration matches the model; the schema check runs over the migrated schema.
4. Apply it: `make migrate` on the laptop; the fleet runs the image's `migrate` command for every tenant unit.

Migrations are never written by hand and never edited after they are committed; a correction is a new migration.


## A change and its audit event in one transaction (T9)

```csharp
await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
order.Approve(reason);
await orders.SaveChangesAsync(cancellationToken);
await audit.WriteAsync(new AuditEvent("procurement.order.approved", AuditOutcome.Success, principal.Id, order.Number, RecordType: "order", RecordId: order.Id, Reason: reason), cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

`IUnitOfWork` (Iron.Data) puts every module context of the request in one transaction; disposing without `CommitAsync` rolls everything back. Without a unit of work, each `SaveChangesAsync` is its own transaction and the audit event is written at once, which is right for a denial or a failed login.

The app role cannot change or delete audit rows; PostgreSQL refuses it. `make migrate` grants the role's privileges when `Database:AppRole` is set (`Database__AppRole=acme_app`); the fleet sets it for every unit.


## Who is acting (T10)

`ICurrentPrincipal` is the signed-in principal of the request (from the `iron.session` cookie), nobody when there is no valid session, and the system principal in jobs and commands. Endpoints declare one of three things: `.RequirePermission(x)`, `.RequireSignedIn()` (any signed-in principal: me, logout) or `.AllowAnonymous()` (health, login, invitation acceptance). The filter answers 401 `auth.not_authenticated` for nobody, 401 `auth.session_expired` for a cookie whose session ran out, and 403 `authz.permission_denied` for the wrong somebody.

On the laptop: `make migrate`, then `make seed` creates the system roles and the first administrator `admin@dev.localhost` in Invited status; the invitation token is in the seed output. Accept it with `POST /auth/invitations/<token>/accept` and a password of at least 12 characters, then `POST /auth/login`; the response sets the cookie and `GET /api/v1/me` shows the permissions and the session expiry.
