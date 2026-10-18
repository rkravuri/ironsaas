# Developer guide

How to work on the repository and how to add the things a product needs: a module, an entity with its migration, an endpoint with its permission, an audit event, a shell page with a grid, a provisioning step. Each section names the ADR behind it. The laptop platform itself is in `local-stack.md`; what a change needs before it is merged is in `definition-of-done.md`.

## Working on the repository

- Develop in WSL 2 (Ubuntu) or on Linux. Clone inside the Linux file system (for example `~/src/foundation`), not under `/mnt/c`; builds are many times faster there.
- Install the .NET SDK 10.0. `dotnet --version` must print 10.0.x. `global.json` accepts any 10.0.1xx feature band.
- Commands: `make build`, `make test`, `make check`, `make format` for the .NET side; `make shell-install`, `make shell-check`, `make shell-a11y`, `make shell-dev` for the shell. `make` alone lists them.
- Before you push: `make test` and `make shell-check` are green and `make format` made no change; CI checks all three.
- Every pull request follows `definition-of-done.md`, summarised in `.github/pull_request_template.md`.

## Layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project
Directory.Packages.props     every package version, once, with its licence
global.json                  SDK band and the test runner
Makefile                     the developer commands
src/
  Kernel/                    technical mechanisms with no business meaning (ADR-0029)
    Iron.Kernel/             the vocabulary, one namespace (no schema)
    Iron.Kernel.Data/  .Api/  .Telemetry/  .Security/                kernel libraries (no schema)
    Iron.Kernel.<Module>/    one folder per kernel module, schema kernel_<module>
      Api/                   endpoints (thin)
      Services/              business logic (plain classes)
      Data/                  entities, repositories, migrations for this module's schema
      Contracts/             public interfaces and DTOs; its own project Iron.Kernel.<Module>.Contracts
      README.md              what the module is responsible for
  Business/                  Iron.Business.<Module>: shared business masters, schema business_<module> (README; empty)
  Products/                  Iron.Products.<Product>: one product each, schema products_<product> (README; empty)
  Hosts/
    Iron.Host.Api/  Iron.Host.Worker/            tenant unit hosts, with their Dockerfiles
    Iron.Host.ControlPlane/  Iron.Host.Fleet/    control plane hosts, with their Dockerfiles
shell/                       the React shell: src/api, src/auth, src/modules/<layer>/<module>, src/components/data-grid, e2e/
tests/                       Iron.Architecture.Tests (the build checks), Iron.Host.Tests, and <Layer>/<Project>.Tests
tools/Iron.Smoke/            the smoke test; scaffolding tools come after S1
infra/compose/               the laptop platform and the unit template's rendered output
.github/workflows/           ci.yml and release.yml
docs/                        prd/, adr/, guides/, slices/
```

## Rules the build enforces

| Rule | When it fails | What to do |
|---|---|---|
| Warnings are errors | Any compiler or analyzer warning | Fix the warning. Do not suppress it without a comment that says why. |
| One version per package, in `Directory.Packages.props` | A project has a `Version` attribute on a `PackageReference`, or a package is missing from the props file | Add the package to `Directory.Packages.props` with its licence; reference it by name only. |
| Layers (ADR-0029) | A project declares no `<IronLayer>` or one that is not `Kernel`, `Business`, `Products`, `Host`, `Test` or `Tool` (the build stops with `IRON0001` or `IRON0002`); the layer does not match the project's name and folder; a project references a higher layer (Kernel to Business or Products, Business to Products), even its Contracts | Declare the layer. For an upward need, invert it: the lower layer declares an interface in its Contracts, the higher one implements it. |
| Module boundaries (ADR-0001) | A module references another module's implementation project; a Contracts project references anything but `Iron.Kernel`; a kernel library references a module; a host references another host | Move the shared type into the other module's `Contracts` project and reference that. |
| Private namespaces (ADR-0001) | An assembly uses a type from another module's `Api`, `Services` or `Data` namespace | Expose what you need through the other module's `Contracts` project. |
| Layout | A project is not in a folder with its own name; a Contracts project is outside its module; a module has no Contracts project; a project under `src/` does not start with `Iron.`; `src/` holds something other than `Kernel/`, `Business/`, `Products/` and `Hosts/` | Move or rename the project; add the missing Contracts project. |

The messages name the project file or the assembly and the forbidden reference.

## Writing code: the kernel vocabulary's conventions (ADR-0007)

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
- Tests that touch the database use the `PostgresFixture` pattern from `tests/Kernel/Iron.Kernel.Data.Tests`: a throw-away container, `EnsureCreated` from the context, `FakeTimeProvider` and a fixed principal.

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

Start with the placement test (ADR-0029). It has no business meaning: Kernel. Two or more products would need to own it: Business. One product owns it: Products. Write the answer and the reason in the module's README; a reviewer reads it first.

Example: a new kernel module `Iron.Kernel.Settings` (typed settings know nothing about any business domain).

1. Create `src/Kernel/Iron.Kernel.Settings/` with the folders `Api/`, `Services/`, `Data/` and `Contracts/`.
2. Copy `src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj` to `src/Kernel/Iron.Kernel.Settings/Iron.Kernel.Settings.csproj`. Change the Contracts reference to `Contracts/Iron.Kernel.Settings.Contracts.csproj`. Keep `<IronLayer>Kernel</IronLayer>` and the `DefaultItemExcludes` line; the second keeps the Contracts sources out of this project.
3. Copy `src/Kernel/Iron.Kernel.Audit/Contracts/Iron.Kernel.Audit.Contracts.csproj` to `src/Kernel/Iron.Kernel.Settings/Contracts/Iron.Kernel.Settings.Contracts.csproj`.
4. Add both projects to `Foundation.slnx` under a new folder `/src/Kernel/Iron.Kernel.Settings/`.
5. Add both projects to the references of `tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj`, so the private-namespace check can read their assemblies. The test tells you when you forget.
6. Reference the module from the hosts that run it (`src/Hosts/Iron.Host.*`).
7. Write `src/Kernel/Iron.Kernel.Settings/README.md`: its layer and why, one paragraph on the module's responsibility, taken from PRD section 2.1, and its schema name (`kernel_settings`).
8. Run `make check`. It confirms the layers, the layout and the references.
9. Then follow the module pattern (from T7): the module class, its permissions, its DbContext with `[Schema("kernel_settings")]` and migrations, its endpoints, its tests in `tests/Kernel/Iron.Kernel.Settings.Tests/`.

Permission names, audit events, error codes, routes and configuration sections name the module, not the layer: `settings.values.manage`, never `kernel.settings.values.manage`.

When a kernel module needs something only a business module can supply (the organisation kinds, for one), the kernel declares an interface in its Contracts with a kernel default, and the business module implements it; the host registers the business implementation. The kernel never references Business.

## Add a business module or a product

The same steps under `src/Business/Iron.Business.<Module>/` (layer `Business`, schema `business_<module>`) or `src/Products/Iron.Products.<Product>/` (layer `Products`, schema `products_<product>`), with tests in `tests/Business/` or `tests/Products/`. Every entity is a `BusinessRecord` or marked `ITenantWide` (TF-220). A business module may use the kernel; a product may use Business and the kernel; both only through Contracts projects. The two layer READMEs (`src/Business/README.md`, `src/Products/README.md`) have the naming table.

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
2. Generate the migration: `make ef-add MODULE=Authorization NAME=AddRoleColour`. The first time, `dotnet tool restore` installs `dotnet-ef` from `.config/dotnet-tools.json`. `MODULE` is the last segment of the module's name, whatever its layer. The generated files land in the module's `Data/Migrations` (for example `src/Kernel/Iron.Kernel.Authorization/Data/Migrations`); read the `Up` method once, run `make format` (Entity Framework writes a byte-order mark that the format check refuses), then commit them with the entity change.
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

`IUnitOfWork` (Iron.Kernel.Data) puts every module context of the request in one transaction; disposing without `CommitAsync` rolls everything back. Without a unit of work, each `SaveChangesAsync` is its own transaction and the audit event is written at once, which is right for a denial or a failed login.

The app role cannot change or delete audit rows; PostgreSQL refuses it. `make migrate` grants the role's privileges when `Database:AppRole` is set (`Database__AppRole=acme_app`); the fleet sets it for every unit.


## Who is acting (T10)

`ICurrentPrincipal` is the signed-in principal of the request (from the `iron.session` cookie), nobody when there is no valid session, and the system principal in jobs and commands. Endpoints declare one of three things: `.RequirePermission(x)`, `.RequireSignedIn()` (any signed-in principal: me, logout) or `.AllowAnonymous()` (health, login, invitation acceptance). The filter answers 401 `auth.not_authenticated` for nobody, 401 `auth.session_expired` for a cookie whose session ran out, and 403 `authz.permission_denied` for the wrong somebody.

On the laptop: `make migrate`, then `make seed` creates the system roles and the first administrator `admin@dev.localhost` in Invited status; the invitation token is in the seed output. Accept it with `POST /auth/invitations/<token>/accept` and a password of at least 12 characters, then `POST /auth/login`; the response sets the cookie and `GET /api/v1/me` shows the permissions and the session expiry.


## An API client (T11)

A service account gets a token with the client credentials grant and sends it as a bearer:

```bash
curl -s -X POST https://acme.localhost/connect/token -u smoke-client:<secret> -d grant_type=client_credentials
curl -s https://acme.localhost/api/v1/users -H "Authorization: Bearer <access_token>"
```

Tokens live 15 minutes, carry the unit's tenant, and are checked on every request together with the principal behind them. `GET /.well-known/jwks.json` publishes the signing key. On the laptop, `make seed` creates `smoke-client` and `make token` fetches a token from the running API host. A token for another tenant, an expired token or a disabled service account answers 401 with `auth.token_invalid` or `auth.token_expired`.


## A unit's configuration (T12, ADR-0016)

A host reads its configuration from its unit secret in the store, loaded at start by `AddIronSecrets()` just below the environment variables. Inside a module, inject the bound options or `IConfiguration` as usual; never read the store directly. What a unit needs at runtime goes into the keys the fleet writes (`ConnectionStrings:Database`, `Database:AppRole`, `Storage:ContainerSasUrl`, `Tokens:SigningKeyPem`); a new runtime secret is a new key written by the fleet's secrets step and read through configuration, not a new way to fetch it.

Files go through `IObjectStorage` (Iron.Kernel.Files.Contracts): `PutAsync`, `GetAsync`, `DeleteAsync`, with the tenant's container behind it. The container's signature is all a unit holds; it cannot create containers or reach another tenant's.


## The control plane and a provisioning step (T13, T14, ADR-0017 to ADR-0019)

The control plane is a unit like the others, with `Iron.Kernel.Tenancy` (the registry and the tenant API) and `Iron.Kernel.Fleet` (jobs, steps, the worker). A new thing to do for every tenant is a new step:

1. Add a class in `src/Kernel/Iron.Kernel.Fleet/Services/Steps/` implementing `IProvisioningStep`: a `Name`, an `Order`, the `Kinds` of job it belongs to (`Provision`, `Update`, `Deprovision`), and `RunAsync(ProvisioningContext, ...)` returning what it reports.
2. Make it idempotent and resumable: read what an earlier run left in the store or the database before creating anything, and keep nothing generated only in memory (ADR-0018). A step that talks to the unit does it through `IUnitRuntime`, never through Docker directly.
3. Register it in `FleetModule.AddServices` and extend the step list in `ProvisioningTests`; the tests run the steps against real PostgreSQL, Azurite and OpenBao containers with a recording runtime.

The fleet worker needs nothing else: it runs the steps of the job's kind in order, records each with its duration, stops at the first failure, and continues from it on the next request.


## A shell page (T15, T16, D3, ADR-0020, ADR-0021, ADR-0027)

1. Add the API binding in `shell/src/api/<module>.ts`: a Zod schema for the response and a function that calls `request()` with it. For a list, use `listSchema(ItemSchema)` and take the ListQuery string as the parameter.
2. Put the page on a template from `shell/src/components/templates`: `ListPage` for a list (the title, one line, the actions, the filters, and the grid; the density switch comes with it), `RecordPage` for a record (the `RecordHeader` with its status as a word, then the panels behind tabs: `AttachmentPanel`, `HistoryPanel`, `ApprovalPanel`, `CommentPanel`), `WizardPage` for steps, `ConsolePage` for settings, `DashboardPage` for numbers. Nothing here is `EmptyState`; a wait is `LoadingState`; a question is `ConfirmDialog`; a form in a dialog is `FormDialog`. They live in `shell/src/components/panels`.
3. For a list, declare the columns (`field` is the API's field name; `sortable` and `filter` follow the API's field table) and render `<DataGrid columns={...} fetchPage={...} defaultSort={...} label={...} onRowOpen={...} />`; paging, sorting and filtering are server round trips the wrapper builds. Space and Enter select the focused row, a double click or Ctrl+Enter runs `onRowOpen`, and `onSelectionChange` tells the page what is selected.
4. For a form, `useZodForm({ schema, initialValues })` and `<Form form={form} onSubmit={save} submitLabel={...} secondary={...}>` with Mantine inputs bound through `form.getInputProps`. Field names are camelCase; when `save` rejects with an `ApiError`, the API's field problems land on their fields (`items[0].unit_price` becomes `items.0.unitPrice`) and the rest shows at the top with the correlation id. Never two primary actions.
5. Add the route and the navigation entry to the module's manifest in `shell/src/modules/<layer>/<module>/manifest.tsx`, each with its `permission` (`null` for any signed-in principal), and the manifest to `shell/src/modules/index.ts` if it is new. Every string goes through `src/i18n/en.json`.
6. `make shell-check`: a route without a permission fails the strict validation; an `ag-grid-*` import outside the wrapper and a colour by value fail lint. A new component gets its tile in the gallery, and `make shell-a11y` scans it.

A page reads the signed-in principal with `useSession()`; it never checks a permission itself beyond what the manifest declares, since the API's answer is the one that counts. Errors from `request()` are `ApiError` with the correlation id; the error views show it.

## Styling a page (D1, ADR-0024, ADR-0025)

A page uses the components as the theme styles them and names colours by meaning: `color="danger"` on an alert, `c="dimmed"` on a hint, a `var(--iron-...)` variable from `shell/src/design/tokens.ts` when a style needs a value. A hex, `rgb()`, `hsl()`, a CSS colour name or a Mantine palette name (`red`, `gray.6`) in a `style`, `styles`, `vars` or `gradient` object or in `c`, `bg`, `bd`, `color`, `fill` or `stroke` fails lint, and the message says what to use. The title of a moment (the login page, a home page, an opener) says `<Title order={2} variant="display">` to take the display family; the work area keeps the reading family. The gallery (`make shell-dev`, then http://localhost:5173/gallery, with `?scheme=dark` and `?density=compact` to switch) shows every token and every themed component in both schemes and both densities; a new component or token gets its example there, and `make shell-a11y` runs the WCAG 2.1 AA scan on it and leaves a screenshot of each run in `shell/test-results/`. The density setting (`useDensity`) and the reduced-motion hook (`usePrefersReducedMotion`) live in `shell/src/design/`. A drawing is a component from `shell/src/design/illustrations/` (`<Welcome decorative size="md" />`): `sm` in a grid or a loader, `md` on a page, `lg` in the gallery; it is hidden from assistive technology when the text beside it says the same, labelled otherwise. The tenant's mark is `<TenantMark hostname={window.location.hostname} size={24} />` from `shell/src/design/patterns/`. A new drawing follows `docs/design/illustration-checklist.md` and gets its tile in the gallery.

## Proving a change (T17, T18, ADR-0022)

| Level | Command | What it proves |
|---|---|---|
| Unit and integration tests | `make test`, `make shell-check` | the code, on real platform services in containers |
| Accessibility | `make shell-a11y` | the gallery against WCAG 2.1 AA with axe in a real browser; no stack needed |
| Smoke | `make smoke` | the live stack from the outside: provisioning, tokens, cookies, denials, the trace |
| Browser | `make e2e` | login and the grid in a real browser |
| Pipeline's stage | `make integration` | platform and control plane from images, then the smoke |

Every pull request runs the first two levels in CI; every merge to main builds the images and runs the pipeline's stage on the runner. A change to a host or a module reaches a laptop tenant with `make images` and `make update-unit TENANT=<slug>`.
