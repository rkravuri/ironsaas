# Developer guide

How to work on the repository and how to add the things a product needs: a module, an entity with its migration, an endpoint with its permission, its limits and its checks, an audit event, a domain event and its subscriber, an encrypted or tracked field, a signature, a save that waits for a second administrator, a licence, a flag, a setting, a shell page with a grid, a provisioning step. Each section names the ADR behind it. The laptop platform itself is in `local-stack.md`; what a change needs before it is merged is in `definition-of-done.md`.

The sections, in order:

- The repository: Working on the repository, Layout, Rules the build enforces, Write an ADR, Requirements.
- A module and its code: Add a module, Add a business module or a suite's module (with a module in a suite, installed per tenant), Hosts and unit commands, Writing code: the kernel vocabulary's conventions, Writing an entity and a module context, A table and its migration, A change and its audit event in one transaction, Publishing and subscribing to events, Writing an endpoint, An endpoint with a permission, Limits on what an endpoint takes, Who is acting, An API client, A unit's configuration, Observability and security in a host.
- Who may do what: Where things are checked, Scoping an endpoint to a record, Record policies, Visibility tags on a record type, Granting access, Delegating and reviewing access, Working across units, Explaining access and conflicts.
- Records under regulation: An encrypted field, and one found by its exact value; A record type under retention; A record's history; Signing a record; A save that waits for a second administrator; Contributing to an evidence pack.
- What a tenant has: Licensing a module or feature, Feature flags and kill switches, Declaring a setting.
- The control plane: The control plane and a provisioning step.
- The shell: A shell page, Styling a page, A module on the home page and in the header.
- Proving a change: Proving a change, Naming what a test proves.

## Working on the repository

- Develop in WSL 2 (Ubuntu) or on Linux. Clone inside the Linux file system (for example `~/src/foundation`), not under `/mnt/c`; builds are many times faster there.
- Install the .NET SDK 10.0. `dotnet --version` must print 10.0.x. `global.json` accepts any 10.0.1xx feature band.
- Commands: `make build`, `make test`, `make check` for the .NET side; `make shell-install`, `make shell-check`, `make shell-a11y`, `make shell-dev` for the shell; `make format` for both (`dotnet format` and Prettier, ADR-0040). `make` alone lists them.
- Before you push: `make verify` is green (or at least `make test`, `make shell-check`, `make security` and `make coverage`) and `make format` made no change; CI checks them all, both formatters included.
- Main changes only through a pull request with every check green, up to date with main, merged with a merge commit (`gh pr merge --merge`); a push to main is refused, administrators included (ADR-0055, `docs/security/README.md`). A documents-only change still waits for the trace.
- A package change rewrites the `packages.lock.json` of each project that uses it (`dotnet restore`); commit them with the change. CI restores in locked mode and refuses a lock file that does not match.
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
    Iron.Kernel.<Module>/    one folder per kernel module, schema kernel_<module> (Protection has none)
      Api/                   endpoints (thin)
      Services/              business logic (plain classes)
      Data/                  entities, repositories, migrations for this module's schema
      Contracts/             public interfaces and DTOs; its own project Iron.Kernel.<Module>.Contracts
      README.md              what the module is responsible for
  Business/                  Iron.Business.<Module>: shared business masters, schema business_<module> (README; empty)
  Suites/                    one folder per suite (ADR-0043), each with its README
    Procurement/             the eProcurement Suite: Iron.Suites.Procurement.<Module>, schema procurement_<module>
                             (Purchasing, its skeleton, since V2)
  Hosts/
    Iron.Host.Api/  Iron.Host.Worker/            tenant unit hosts, with their Dockerfiles
    Iron.Host.ControlPlane/  Iron.Host.Fleet/    control plane hosts, with their Dockerfiles
shell/                       the React shell: src/api, src/auth, src/modules/<layer>/<module> (a suite's under suites/<suite>/), src/components/data-grid, e2e/
tests/                       Iron.Architecture.Tests (the build checks), Iron.Host.Tests, <Layer>/<Project>.Tests,
                             Iron.Testing (the platform services the tests run on, and [Proves]) with test-services.sh,
                             and Iron.Testing.Analyzers (a test names what it proves)
tools/                       Iron.Smoke (the smoke test), Iron.Analyzers (the build's own rules, IRON0010 and IRON0011
                             among them), Iron.ProcessMaps, and the Python checks (prd_check, trace, the security gate,
                             coverage, the validation pack), each with its tests; scaffolding tools are not built yet
infra/compose/               the laptop platform and the unit template's rendered output
security/                    the security gate's exceptions, licence policy, coverage floors and the repository's rules
.github/workflows/           ci.yml (every pull request), release.yml (nightly), security-report.yml (monthly)
docs/                        prd/, adr/, guides/, slices/, prompts/, security/, trace/, risk/
```

## Rules the build enforces

| Rule | When it fails | What to do |
|---|---|---|
| Warnings are errors | Any compiler or analyzer warning | Fix the warning. Do not suppress it without a comment that says why. |
| One version per package, in `Directory.Packages.props` | A project has a `Version` attribute on a `PackageReference`, or a package is missing from the props file | Add the package to `Directory.Packages.props` with its licence; reference it by name only. |
| Layers (ADR-0029, ADR-0043) | A project declares no `<IronLayer>` or one that is not `Kernel`, `Business`, `Suite`, `Host`, `Test` or `Tool` (the build stops with `IRON0001` or `IRON0002`); a Suite project declares no `<IronSuite>`, another project declares one, or the value is not a suite's name (`IRON0003`, `IRON0004`); the layer or the suite does not match the project's name, folder and schema prefix; a project references a higher layer (Kernel to Business or a suite, Business to a suite), even its Contracts; a suite's project references another suite's | Declare the layer. For an upward need, invert it: the lower layer declares an interface in its Contracts, the higher one implements it. |
| Module boundaries (ADR-0001) | A module references another module's implementation project; a Contracts project references anything but `Iron.Kernel`; a kernel library references a module; a host references another host | Move the shared type into the other module's `Contracts` project and reference that. |
| Private namespaces (ADR-0001) | An assembly uses a type from another module's `Api`, `Services` or `Data` namespace | Expose what you need through the other module's `Contracts` project. |
| What a test proves (ADR-0044) | A test method has neither `[Proves(...)]` nor `[Untraced("why")]` (`IRON0005`), names an identifier out of form (`IRON0006`), or is both (`IRON0007`); a Vitest or Playwright title does not end with the bracketed list (`iron/test-proves`) | Name the criteria or the decision the test proves; see Naming what a test proves. |
| Layout | A project is not in a folder with its own name; a Contracts project is outside its module; a module has no Contracts project; a project under `src/` does not start with `Iron.`; `src/` holds something other than `Kernel/`, `Business/`, `Suites/` and `Hosts/`, or `src/Suites/` a folder that is not a suite with its README | Move or rename the project; add the missing Contracts project. |
| Security rules (ADR-0055) | A .NET security rule (CA5350 to CA5404 and the others of AnalysisModeSecurity All) in the product's code; an eslint-plugin-security rule in the shell's code; one of the repository's rules in `security/rules/` (`make security`) | Fix the code. A false positive is suppressed at its line with the reason beside it: `#pragma warning disable CAxxxx` with a comment, or `// nosemgrep: <rule>` on the line before, with the reason above it. |
| The security gate (ADR-0055) | A critical or high vulnerability in a dependency or an image, a misconfiguration, a licence `security/licences.yaml` does not allow, or an expired exception | Raise the package to its fixed version, or write an exception in `security/exceptions.yaml` with its expiry for the design session to approve; record a licence read by hand in `security/licences.yaml`. |
| Coverage floors (ADR-0055) | Line coverage of the .NET code or the shell below its floor in `security/coverage.yaml`, or a floor lowered | Add the tests that cover what changed; raise the floor when the check says it may. |
| Threat models (ADR-0055) | A module or an anonymous endpoint without a record in `docs/security/threat-models/`, or a record out of the template's form | Write the record from `0000-template.md` before building the module or the interface, naming it. |

The messages name the project file or the assembly and the forbidden reference.

## Write an ADR

Copy `docs/adr/0000-template.md` to the next number, fill it in, and add it to the table in `docs/adr/README.md`. A pull request that introduces a decision without its ADR is not merged (TF-209).

## Requirements (ADR-0041, ADR-0042)

The requirements are in `docs/prd/`, one markdown document per layer and product with a CSV beside it, the Foundation's in `docs/prd/foundation/` and each suite's in `docs/prd/suites/<suite>/` (ADR-0043); `docs/prd/README.md` lists them. The markdown is the source: edit it, run `make prd-csv` to write its CSV, and `make prd-check` before committing (CI runs it).

- A new requirement takes the next free identifier in its area's block of twenty, is tagged `[New]`, and has criteria `AC1 Given ..., When ..., Then ...` with at least one marked `(error case)`. A rule nobody has decided is tagged `[Decision]` with the question and a proposed default in its notes.
- An identifier is never reused, and a moved requirement starts its notes with what it was ("Was FF-022."); `requirements-map.md` says where every requirement of the archived Functional Foundation PRD went.
- A dependency points down the layers (kernel, business, a suite's products, its surfaces) and stays inside its suite; between a suite's products it points to an earlier one, or to a later one only as an event, written `RCV-081 (event)`. The check refuses anything else.
- A test names what it proves by identifier and criterion, `REQ-012 AC2`, so the validation pack can trace it (TF-218).

## Add a module

Start with the placement test (ADR-0029, ADR-0043). It has no business meaning: Kernel. Enterprise products of any kind need it, or the products of two or more suites: Business. Two or more products of one suite need it: that suite's shared master. One product owns it: that product. Write the answer and the reason in the module's README; a reviewer reads it first.

Example: a new kernel module `Iron.Kernel.Settings` (typed settings know nothing about any business domain).

1. Create `src/Kernel/Iron.Kernel.Settings/` with the folders `Api/`, `Services/`, `Data/` and `Contracts/`.
2. Copy `src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj` to `src/Kernel/Iron.Kernel.Settings/Iron.Kernel.Settings.csproj`. Change the Contracts reference to `Contracts/Iron.Kernel.Settings.Contracts.csproj`. Keep `<IronLayer>Kernel</IronLayer>` and the `DefaultItemExcludes` line; the second keeps the Contracts sources out of this project.
3. Copy `src/Kernel/Iron.Kernel.Audit/Contracts/Iron.Kernel.Audit.Contracts.csproj` to `src/Kernel/Iron.Kernel.Settings/Contracts/Iron.Kernel.Settings.Contracts.csproj`.
4. Add both projects to `Foundation.slnx` under a new folder `/src/Kernel/Iron.Kernel.Settings/`.
5. Add both projects to the references of `tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj`, so the private-namespace check can read their assemblies. The test tells you when you forget.
6. Reference the module from the hosts that run it (`src/Hosts/Iron.Host.*`).
7. Write `src/Kernel/Iron.Kernel.Settings/README.md`: its layer and why, one paragraph on the module's responsibility, taken from the technical PRD's section 2.1 (a business module's from the Business PRD's section 9, a product's from its PRD's section 13), and its schema name (`kernel_settings`).
8. Run `make check`. It confirms the layers, the layout and the references.
9. Then follow the module pattern (from T7): the module class, its permissions, its DbContext with `[Schema("kernel_settings")]` and migrations, its endpoints, its tests in `tests/Kernel/Iron.Kernel.Settings.Tests/`.

Permission names, audit events, error codes, routes and configuration sections name the module, not the layer: `settings.values.manage`, never `kernel.settings.values.manage`.

When a kernel module needs something only a business module can supply (the organisation kinds, for one), the kernel declares an interface in its Contracts with a kernel default, and the business module implements it; the host registers the business implementation. The kernel never references Business.

## Add a business module or a suite's module

The same steps under `src/Business/Iron.Business.<Module>/` (layer `Business`, schema `business_<module>`) or `src/Suites/<Suite>/Iron.Suites.<Suite>.<Module>/` (layer `Suite` with `<IronSuite><Suite></IronSuite>`, schema `<suite>_<module>`, for example `procurement_orders`), with tests in `tests/Business/` or `tests/Suites/<Suite>/`. Every entity is a `BusinessRecord` or marked `ITenantWide` (TF-220). A business module may use the kernel; a suite's module may use Business and the kernel; both only through Contracts projects. A suite never references another suite. Business never depends on a suite: where it must hear from one, it declares an extension point the suite's module implements. Inside a suite the shared masters come first, then the products in process order, for the eProcurement Suite Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing: a later product may use an earlier one's Contracts, and an earlier one hears from a later one only through domain events (ADR-0042). The layer READMEs (`src/Business/README.md`, `src/Suites/README.md`) have the naming tables, the suite's README (`src/Suites/Procurement/README.md`) its products, and the module's PRD names its schema, its permissions and its dependencies.

**A module in a suite is installed per tenant** (TF-244, ADR-0059). A unit loads it only where its tenant bought the suite, so a suite's module joins its suite rather than the hosts' lists: it is listed in its suite's `SuiteDefinition`, which the hosts carry, and the unit's `Suites:Installed` decides whether it runs. Where it does not, it has no endpoints, no schema and no seed, and the shell shows nothing of it; so a seed brings only what the suite needs, and nothing of the Foundation depends on it. It is licensed per module (see Licensing a module or feature), and the suite's plans name it: a plan names only its own group's modules and features, and the control plane refuses one that names another's. The steps, from the placement test to the plans and the shell's manifest, are in `src/Suites/README.md`, "Adding a module to a suite"; `Iron.Suites.Procurement.Purchasing` is the reference, its tests the pattern for a test host with and without the suite (`AboutEndpointTests`).

## Hosts and unit commands (ADR-0011)

A host's `Program.cs` is the only place that decides what runs in a process. It has one shape:

1. `HealthProbe.IsCommand(args)`: answer the `health` command without building the host.
2. `TenantBinding.FromConfiguration`: bind to one tenant or stop.
3. List the modules: the Foundation's, then the modules of the suites the unit's `Suites:Installed` names (`InstalledSuites.Compose`, TF-244); the API's are `UnitModules.ForUnit(configuration)`, which adds the sample module where `Samples:Enabled` says so (ADR-0057).
4. Register the shared libraries, then `AddModules`.
5. Build; if `DatabaseCommands.IsCommand(args)`, run `migrate`, `seed` or `schema-check` and exit.
6. Pipeline order: `UseIronTelemetry`, `UseIronApi`, `UseIronSecurity`, `UseIronTenant`, then endpoints.

Adding a Foundation module to a host means adding one line to the list (`UnitModules.Foundation()` and the worker's); a suite's module is added to its suite's definition instead. Adding reference data means one `ISeeder` registered by the module. Adding a table means a migration in the module's project; `migrate` picks it up because the module context is registered with `AddIronDbContext`.

On the laptop: `make run-api` serves http://localhost:5100 bound to tenant `dev` for hostnames `localhost` and `dev.localhost`; `make run-worker` runs the worker on http://localhost:5101 with a heartbeat every 15 seconds; `make migrate` runs the migrate command against the laptop database; `make images` builds the five images (four hosts and the shell).

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
- Tests that touch the database get a database of their own from `TestPostgres` (`tests/Iron.Testing`, see Proving a change), as `tests/Kernel/Iron.Kernel.Data.Tests/PostgresFixture.cs` does: create the tables from the context, then use `FakeTimeProvider` and a fixed principal.

## A table and its migration (ADR-0008)

1. Add or change the entity in the module's `Data/` folder (derive from `AuditedEntity`, `AppendOnlyEntity` or `BusinessRecord`) and map it in `ConfigureModule`.
2. Generate the migration: `make ef-add MODULE=Authorization NAME=AddRoleColour`. The first time, `dotnet tool restore` installs `dotnet-ef` from `.config/dotnet-tools.json`. `MODULE` is the last segment of the module's name, whatever its layer. The generated files land in the module's `Data/Migrations` (for example `src/Kernel/Iron.Kernel.Authorization/Data/Migrations`); read the `Up` method once, run `make format` (Entity Framework writes a byte-order mark that the format check refuses), then commit them with the entity change.
3. `make test`: the module's `ModelTests` passes only when the migration matches the model; the schema check runs over the migrated schema.
4. Apply it: `make migrate` on the laptop; the fleet runs the image's `migrate` command for every tenant unit.

Migrations are never written by hand and never edited after they are committed; a correction is a new migration. The one change made by hand is a rename, before the first commit: `make ef-add` cannot tell a rename from a drop and an add, so turn its drop and add into `RenameColumn` and say so in the migration's comment (ADR-0031 records the first, `RoleCodeAndName`).

## A change and its audit event in one transaction (ADR-0013)

```csharp
await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
order.Approve(reason);
await orders.SaveChangesAsync(cancellationToken);
await audit.WriteAsync(new AuditEvent("procurement.order.approved", AuditOutcome.Success, principal.Id, order.Number, RecordType: "order", RecordId: order.Id, Reason: reason), cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

`IUnitOfWork` (Iron.Kernel.Data) puts every module context of the request in one transaction; disposing without `CommitAsync` rolls everything back. Without a unit of work, each `SaveChangesAsync` is its own transaction and the audit event is written at once, which is right for a denial or a failed login.

The app role cannot change or delete audit rows; PostgreSQL refuses it. `make migrate` grants the role's privileges when `Database:AppRole` is set (`Database__AppRole=acme_app`); the fleet sets it for every unit.

The unit's worker seals each event into the tenant's chain a few seconds after its commit (ADR-0050); a module writes events exactly as before and never sets a chain column. A table the fleet's verifier must read is an entity marked `IVerifierReadable`; nothing outside the audit chain should be.

**Times are the server's** (TF-065, ADR-0050). `CreatedAt` and `UpdatedAt` are stamped from `TimeProvider` on save, whatever a caller set. A request body never carries a time the server records: take `starts_at` or `due_on` only for a period a person chooses, and add it to `ChosenTimes` in `tests/Iron.Host.Tests/HostTests.cs` with why; any other date in a request body fails that test.

**An event a security alert should count** (TF-079, ADR-0056). Declare its type in the module's `*AuditEvents.Types`; a type nobody declares makes every rule that counts it fail. An export writes its event with `AuditDetails.Rows`, and a change that touches a critical area marks itself with `AuditDetails.Critical` when the rule cannot tell from the type alone; the writer adds the client's address to a refusal by itself. A new rule goes into `AlertRules.All` in Iron.Kernel.Monitoring with a section of its own in `docs/security/incident-response.md`, which `RunbookTests` checks.

## Publishing and subscribing to events (TF-085, TF-110 to TF-112, ADR-0060)

A module tells others of a change by publishing a domain event, in the change's own transaction, and never knows who listens. It is how an earlier module or product hears from a later one (ADR-0042). An audit event says who did what for the record; a domain event says what happened for the modules that react.

**Declare the event** in the module's Contracts, with a record for its data, and list it in `IModule.Events`:

```csharp
public sealed record SampleRecordCreated(Guid RecordId, Guid UnitId);

public static class SamplesEvents
{
    public static readonly EventType<SampleRecordCreated> RecordCreated = new(
        "samples.record.created", 1, "sample_record", "A sample record was created, in a unit marked for testing.", "Platform tests");

    public static IReadOnlyList<EventType> All => [RecordCreated];
}
```

The name is `module.record.happened`, in the past tense. The data carries identifiers, not copies: `Guid` fields, codes (`string` or an enum, no spaces, at most 100 characters), numbers, flags, times, days, and lists of identifiers or codes. A name, an address, a description or a nested record is refused when the event is declared, and so is `[PersonalData]` on anything but an identifier; mark every field that names a person with it. The catalogue lists the event, its fields and its subscribers (`make catalogue`, and the host test that keeps the file equal to the code).

**Change it by versions.** Adding an optional field (a nullable property) keeps the version. Anything else is a new `EventType` with the same name and the next version; mark the old one `.Deprecated(new DateOnly(2027, 1, 31), replacedBy: 2)`, publish both until that day and until no subscriber reads the old one, then remove it. The catalogue lists both, the old with its day.

**Publish inside the unit of work**, after the change and beside its audit event:

```csharp
await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
context.Records.Add(record);
await context.SaveChangesAsync(cancellationToken);
await events.PublishAsync(SamplesEvents.RecordCreated, record.Id, new SampleRecordCreated(record.Id, unit), cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

`IEventPublisher` (Iron.Kernel.Events.Contracts) takes the actor, the person acted for, the tenant and the correlation id from the request or the job; the record id is the record the event is about, and the order per record follows it. Outside a unit of work it throws, and so does data that does not match its declaration: both are a programmer's error, which a test finds. A rollback takes the event with it.

**Subscribe** with a handler in the subscribing module and a subscription in its `IModule.Subscriptions`:

```csharp
public IReadOnlyList<EventSubscription> Subscriptions =>
[
    EventSubscription.To<SampleRecordCreated, NoticeCreatedSample>(SamplesEvents.RecordCreated, "samples.notice_created", "Marks a new sample record noticed."),
];
```

The subscription's name is `module.what_it_does`; it is the inbox's key, so it never changes once deployed. The handler, `IEventHandler<T>`, reads the publisher's event through its Contracts and changes only its own module's records, in its own context; the dispatcher runs it in a unit of work that also marks the delivery handled, so what it changed and the record that it ran commit together, and a repeated delivery does not run it again. Return a failure, or throw, to roll it back and retry: 2, 4, 8, 16 and 32 seconds, then a dead letter with the error, audited and alerted (`event_dead_letters`). A handler must still be idempotent in what it touches outside the unit's database, such as a webhook. Load what you need by the event's identifiers, and treat a record that is gone as done. A suite's subscription is held, not retried, while its tenant does not run the suite: its waiting deliveries show "Held: suite removed" and resume by themselves when the suite is added again, so a suite's handler must not assume the events it reads are recent.

**Test it** on the module's fixture: publish in a unit of work, run `EventDispatcher.RunOnceAsync` (the worker's pass) until it does no work, and check the handler's change. `tests/Kernel/Iron.Kernel.Events.Tests` shows the patterns: a rollback, a crash between commit and dispatch, a repeated delivery, order per record, a failing subscriber. A host that runs a publishing module runs the Events module too, before the modules that publish (`UnitModules`, the control plane's composition, and a test host's list); only the unit's worker and the fleet call `AddEventDispatch()`.

**Inspect and replay.** The console's Domain events page (`/console/events`, `events.events.read`) searches by event, record, correlation id and day, shows each subscriber's state and the dead letters; with `events.events.replay` and a recent sign-in, and a reason, an administrator replays an event, or a range of positions, to one subscriber. The operator does the same with `make events TENANT=x` and `make events-replay`. The runbook's "Dead-lettered events" section says what to do with one.

## Writing an endpoint (ADR-0009)

- Group the resource with `endpoints.MapApiGroup("users")`; the path becomes `/api/v1/users`. Resource names are plural kebab-case nouns.
- Handlers return `IResult`. A service result goes through `ApiResults.From(result, http, value => TypedResults.Ok(value))`; a failure becomes problem details on its own.
- Lists: declare the fields once in a `ListFields<T>` (snake_case names, pointing at the entity properties, with a default sort), parse with `ListQueryParser.Parse(http.Request.Query, fields)`, apply with `.ApplyListQuery(query, fields).ToPageAsync(query.Page, ct)` and return `ApiResults.List(page, http, query)`. That works for an entity marked `IOwnAccessRule` (its module applies its own access rule; `ITenantWide` is one); a business record pages through its policy (see Record policies).
- Request bodies: write an `AbstractValidator<TRequest>`, register it in the module's `AddServices`, and add `.Validate<TRequest>()` to the endpoint.
- Never catch exceptions to return an error status; let the unexpected exception handler produce the 500 problem with the correlation id. Return `Error`s for expected failures.
- Declare every success the handler answers on the mapping, since an `IResult` tells the OpenAPI document nothing: `.Produces<ListResponse<UserResponse>>(StatusCodes.Status200OK)` for a list, `.Produces<UserResponse>(StatusCodes.Status201Created)` for a create, `.Produces(StatusCodes.Status204NoContent)` for a delete. The host test refuses an operation that does not say what it needs, or answers a success without a schema (TF-100 AC3).
- The OpenAPI document and the Scalar browser are at `/openapi/v1.json` and `/scalar/v1` in Development.

## An endpoint with a permission (ADR-0012)

1. Declare the permission in the module's Contracts project: `public static readonly Permission UsersRead = new("identity.users.read", "List and view users", Risk.Low);` and list it in the module's `Permissions`.
2. Put it on the endpoint or the group: `endpoints.MapApiGroup("users").RequirePermission(IdentityPermissions.UsersRead);`.
3. Run the host. It starts only when every endpoint has a permission or `.AllowAnonymous()`; the message names what is missing.
4. The filter runs before the handler: 401 for nobody, 403 `authz.permission_denied` (with the permission named) for a principal without it, and a denial audit event. OpenAPI shows "Requires permission" on the operation.

Anonymous endpoints are the short list of TF-030: health, login, recovery, invitation acceptance, public branding. `Iron.Host.Tests` keeps the list fixed; adding to it is a reviewed change.

## Limits on what an endpoint takes (TF-071, TF-072, ADR-0053)

- **Every field has a limit.** A string has its `MaximumLength`, a collection its `MaximumCount` (and its items their own limits), a free JSON value a maximum size, and an object a validator of its own; numbers, dates, identifiers and enumerations are limited by their type. Declare them in the endpoint's validator (`.Validate<T>()`); a body of only typed fields needs none, and a body the handler reads itself declares a request size limit. `RequestLimitCheck` stops a host that takes a field without one, so the host tests fail the build. The query string is limited for every endpoint: 50 parameters of 2,000 characters.
- **Rich text** is a `RichText` (`Iron.Kernel.Security.Text`), made with `RichText.From(html)`, which keeps the formatting the allow-list names and drops the rest, scripts included; its rule is `.MaximumLength(n)`. The shell renders HTML only in `RichTextView`; lint refuses it anywhere else.
- **A file name** a person gives is display text only: the Files module stores the bytes under a random key and keeps the name normalised (`FileNames` in its services), so a module never builds a path from it.
- **SQL is never made from strings.** Write the query with Entity Framework, or a constant command with typed parameters; the analyzers refuse SQL built from text (IRON0010, IRON0011). A helper that takes its SQL as a parameter carries `[SqlFromIdentifiers("why")]`.
- **A rate limit** is a marker on the endpoint: `.RateLimited(RateLimitPolicies.Exports)` for an export. Every path under `/api`, `/auth` and `/connect` is under the API's limit already, and sign-in and recovery have theirs; the limits, their defaults and who sets them are in the Protection README. A refusal is 429 `protection.rate_limited` with `Retry-After`, which `request()` turns into words.

## Who is acting (ADR-0014)

`ICurrentPrincipal` is the signed-in principal of the request (from the `iron.session` cookie), nobody when there is no valid session, and the system principal in jobs and commands. `ActingForId` is the person a shell session acts for by delegation (ADR-0037), and null otherwise. Endpoints declare one of three things: `.RequirePermission(x)`, `.RequireSignedIn()` (any signed-in principal: me, logout) or `.AllowAnonymous()` (health, login, invitation acceptance). The filter answers 401 `auth.not_authenticated` for nobody, 401 `auth.session_expired` for a cookie whose session ran out, and 403 `authz.permission_denied` for the wrong somebody.

**A high-risk action asks for a recent authentication** (TF-248, ADR-0047). Declare it after the permission, so a caller without the permission hears that first:

```csharp
users.MapPost("/{id:guid}/deactivate", DeactivateAsync)
    .RequirePermission(IdentityPermissions.UsersManage)
    .ScopedByRow()
    .RequireRecentAuthentication(); // five minutes unless a number says otherwise
```

Where the data decides whether the action is high-risk, the service asks the kernel's `IRecentAuthentication` itself, last, after every other check: `if (recent.Require() is { IsFailure: true } stale) return stale.Error;`, as granting a role with a high-risk or critical permission does. A setting whose change is high-risk is declared with `.RequiringRecentAuthentication()`, and `SettingsWriter` asks for it. The refusal is 403 `auth.reauthentication_required`; the shell's `request()` opens its one re-authentication dialog and sends the request again once the person confirmed it is them, so a page needs nothing of its own. A session's authentication is its sign-in or its latest confirmation, a token's its issue; jobs, seeds and commands are never asked.

**A required step** comes before every endpoint (ADR-0047, ADR-0049). When a signed-in person has a document version to accept, or the tenant's MFA policy now requires a second factor of a person who has none, the filters answer 403 with the step's code, `identity.terms.acceptance_required` first, then `identity.mfa.enrolment_required`, for every endpoint but those that name the step: `.AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired)` for the factors and re-authentication, `.AllowDuringRequiredStep(IdentityErrors.TermsAcceptanceRequired)` for the terms and retiring a document. Who am I, the shell's context and sign out declare `.AllowDuringRequiredStep()` with no step, which passes every step. A new endpoint is part of a step only if a person must reach it to take that step; nothing else declares it. The shell's frame takes the person to the step's page on that answer.

**Impersonation** (TF-025, ADR-0049). While someone is signed in as another person, `ICurrentPrincipal` is the person, and `ImpersonatorId` and `ImpersonatorName` name who is signed in as them; the audit writer puts both on every event, so a module writes nothing of its own. An impersonator never confirms it is them: `IRecentAuthentication` refuses with `auth.impersonation_forbidden`, so whatever asks for step-up, a signature or a second approval is closed to them without a line of its own. An endpoint that is the person's own business, and not behind step-up (their password, factors, sessions, terms, acting for someone), declares `.ForbiddenDuringImpersonation()`.

On the laptop: `make migrate`, then `make seed` creates the system roles and the first administrator `admin@dev.localhost` in Invited status; the invitation link is on the seed's console, not in its log (ADR-0046). Accept it by opening the link in the shell, or with `POST /auth/invitations/accept` and the token from the link's fragment and a password of at least 12 characters, then `POST /auth/login`; the response sets the cookie and `GET /api/v1/me` shows the permissions and the session expiry.

## An API client (ADR-0015)

A service account gets a token with the client credentials grant and sends it as a bearer:

```bash
curl -s -X POST https://acme.localhost/connect/token -u smoke-client:<secret> -d grant_type=client_credentials
curl -s https://acme.localhost/api/v1/users -H "Authorization: Bearer <access_token>"
```

Tokens live 15 minutes, carry the unit's tenant, and are checked on every request together with the principal behind them. `GET /.well-known/jwks.json` publishes the signing key. On the laptop, `make seed` creates `smoke-client` and `make token` fetches a token from the running API host. A token for another tenant, an expired token or a disabled service account answers 401 with `auth.token_invalid` or `auth.token_expired`.

## A unit's configuration (ADR-0016)

A host reads its configuration from its unit secret in the store, loaded at start by `AddIronSecrets()` just below the environment variables. Inside a module, inject the bound options or `IConfiguration` as usual; never read the store directly. What a unit needs at runtime goes into the keys the fleet writes (`ConnectionStrings:Database`, `Database:AppRole`, `Storage:ContainerSasUrl`, `Keys:Prefix`); a key is never one of them: it stays in the key service, used through `IKeyService` (ADR-0054); a new runtime secret is a new key written by the fleet's secrets step and read through configuration, not a new way to fetch it.

Files go through `IObjectStorage` (Iron.Kernel.Files.Contracts): `PutAsync`, `GetAsync`, `DeleteAsync`, with the tenant's container behind it. The container's signature is all a unit holds; it cannot create containers or reach another tenant's.

## Observability and security in a host (ADR-0010)

- `builder.AddIronTelemetry("iron-api", t => t.TenantId = ...)` then `app.UseIronTelemetry()` first in the pipeline. Every request gets a trace, the response carries `X-Correlation-Id`, and metrics and logs flow to the collector. Set `OTEL_EXPORTER_OTLP_ENDPOINT` when the collector is not on `localhost:4317`.
- `builder.Services.AddIronSecurity(builder.Configuration)` then `app.UseIronSecurity()` after `UseIronApi()`. Allowed browser origins go in `Security:Cors:AllowedOrigins`; the shell needs none.
- `builder.Services.AddIronHealth()` plus `app.MapIronHealth()`; a module or infrastructure adds readiness checks with the tag `ready` (`AddIronDatabaseHealthCheck()` for PostgreSQL).
- Log through `ILogger<T>` with structured values (`logger.LogInformation("Tenant {TenantId} provisioned", id)`); never string-concatenate values into the message. Do not log secrets or personal data: log the person's id, never their name or email (TF-069). The scrubbing rule masks a property named like a name, an email, a phone or an address, and any email shape, so name a property for what it holds: a URL is `{Url}`, not `{Address}`. A log line is never the record of an action; its audit event is.
- `builder.Services.AddIronClockCheck(builder.Configuration)` measures the process's clock (TF-065 AC3); every host has it.

## Where things are checked

A request meets the checks in this order; **Explain access** in the console (`/console/explain`) shows the entitlements, the flags, the permission and the record policy for a person and an action, from the same decisions (ADR-0039).

| Check | A module declares | Checked | Refused with | Section |
|---|---|---|---|---|
| Rate limits | `.RateLimited(policy)` for a limit of its own; every path has the API's | after authentication, before the endpoint (`UseIronProtection`) | 429 `protection.rate_limited` with `Retry-After` | Limits on what an endpoint takes |
| Entitlements | its licence in `IModule`, its features in Contracts; `.RequireFeature(code)` on an endpoint | after the rate limits, before the endpoint's other checks; `IEntitlements` in code | 403 `entitlements.module_disabled`, `entitlements.read_only`, `entitlements.seat_required` or `entitlements.feature_disabled` | Licensing a module or feature |
| Flags and kill switches | its flags in Contracts; `.RequireFlag(key)`, `.RequireFlagOff(KillSwitches.Imports.Key)` | after the entitlements, before the permission; `IFlagClients` in code | 404 `flags.disabled`, 503 `flags.killed` | Feature flags and kill switches |
| Permission at a scope | its permissions in Contracts; `.RequirePermission(p)` with `.ScopedToUnit`, `.ScopedToRecord` or `.ScopedByRow` | the endpoint filter, after the flags, with the delegator's duties while acting | 401 `auth.not_authenticated`, 403 `authz.permission_denied` | An endpoint with a permission; Scoping an endpoint to a record |
| Record policy | `services.AddRecordPolicy(...)` for each business record | in the query: `IRecordPolicies.ApplyAsync` for a list, `FindAsync` for one record; then the reader's visibility tags, in the same query | 404 `data.record.not_found`, 403 `data.record.forbidden` | Record policies; Visibility tags on a record type |
| Request limits | each field's limit in the endpoint's validator | at the host's start (`RequestLimitCheck`), then on every body (`.Validate<T>()`) | the host does not start; 400 with the field's error | Limits on what an endpoint takes |
| Recent authentication | `.RequireRecentAuthentication()` after the permission, or `IRecentAuthentication` in the service | after the permission; in a service, last | 403 `auth.reauthentication_required`, which the shell's dialog answers | Who is acting |
| Conflict rules | nothing: the tenant's administrators write the rules | when a role is granted, a duty delegated or a role's permissions changed, in the service | 409 `authz.sod.conflict` for a rule that blocks; a rule that warns lists the conflict in the report | Explaining access and conflicts |
| Signatures | its signable types (`ISignableRecords`) and the snapshot; the tenant sets the policy per type | in the service, the signer proved before the transaction | 403 with the reason, such as `compliance.signature.required` or `compliance.signature.impersonation` | Signing a record |
| Four-eyes | the area of its sensitive saves; `IFourEyes` after its own checks | in the service, last, before it writes, for the areas the tenant chose | 202 with the held change, applied when a second administrator approves | A save that waits for a second administrator |

A check a handler makes on its own (a move's new parent, the no-escalation rule of a grant) comes after these and is not explained; its error says why. Some rules are checked before any request: the build refuses SQL made from strings (IRON0010, IRON0011), a test that names nothing (IRON0005 to IRON0007), the layers (IRON0001 to IRON0004) and the .NET security rules, and a host does not start with an endpoint without its permission or a request field without its limit (see Rules the build enforces).

## Scoping an endpoint to a record (ADR-0030)

A permission is checked tenant-wide unless the endpoint says where the access happens. Once it does, a role assigned to an organisation unit applies to that unit's records and not to others, and a role assigned with children applies to the whole subtree, wherever it moves later.

1. **One record by its id.** Name the record type (a short snake_case word, the same one record-scoped assignments use) and give the lookup that finds the unit that owns the record, one query by key:

   ```csharp
   orders.MapPost("/{id:guid}/approve", ApproveAsync)
       .RequirePermission(OrdersPermissions.Approve)
       .ScopedToRecord("purchase_order", (services, id, ct) => services.GetRequiredService<OrdersDbContext>().Orders
           .Where(o => o.Id == id).Select(o => (Guid?)o.OwningOrganisationId).SingleOrDefaultAsync(ct));
   ```

   A record the lookup does not find is checked tenant-wide, so only a tenant-wide grant reaches the handler, which answers 404.
2. **An operation on a unit itself:** `.ScopedToUnit("id")`, as the organisation unit endpoints do.
3. **A second place the route cannot carry** (a target in the body): check it in the handler with `IPermissionEvaluator` and `new AccessRequest(...) { Scope = AccessScope.Unit(target) }`, as the unit move does for its new parent.
4. **A list:** declare `.ScopedByRow()` on the endpoint, so the filter lets in a principal who holds the permission at any scope; then ask once and filter once. For a business record that is its policy (see Record policies); for other rows, as here. `var access = await recordAccess.AccessibleAsync(principal.Id, OrdersPermissions.Read, "purchase_order", ct);` then `query.WhereAccessible(access)` before `ApplyListQuery`. Rows that are not business records name their unit another way: `query.WhereAccessible(access, p => p.HomeUnitId, p => p.Id)`. The same filtered query answers a read by id, with 404 outside. The answer costs one query over the units whatever the list's length (or none for a tenant-wide grant), and the list stays one query. Never check row by row.
5. **Test it** with an assignment scoped to a unit: a record of that unit is allowed, a record of a sibling is denied, and a record of a child is allowed only with children (see `ScopeEvaluationTests` in the Authorization tests).

Saving a business record also checks its owner: `OwningOrganisationId` must name an active unit, or the save throws `OwningOrganisationException`. Check the unit in the service first and answer with an error; the interceptor is the guarantee behind it.

## Record policies (ADR-0032)

Every business record type has a policy; its lists and reads by id go through it. Forgetting is not possible: paging a raw query of a business record does not compile (CS0311: it is not `IOwnAccessRule`), and a business record type without a registered policy fails the architecture tests.

**A policy in a few lines**, in the module's `AddServices`. An order is seen in the reader's units, approved where the reader may approve, and changed by its creator while a draft:

```csharp
services.AddRecordPolicy(
    "purchase_order",                       // the record type name record-scoped assignments use, its code
    "Purchase orders",                      // what people call these records, as a merge preview shows them
    OrdersPermissions.Read,                 // the scope rule for this permission always applies first
    RecordRules.All(
        RecordRules.UnitVisibility<Order>(OrdersPermissions.Read, (OrdersActions.Approve, OrdersPermissions.Approve)),
        RecordRules.OwnerWhile<Order>(o => o.Status == OrderStatus.Draft, RecordActions.Update)));
```

`UnitVisibility` reads the units the read permission reaches and allows each action it names where that action's permission reaches; `OwnerWhile` lets the creator perform its actions while the module's condition holds, and does not narrow reading. `All` takes the strictest answer, `Any` the most generous; an action no block has a rule for is denied. The kernel joins the policy to the scope rule with AND, so a policy can hide records and never show more. A policy class of the module's own implements `IRecordPolicy<T>` and registers with `AddRecordPolicy<Order, OrderPolicy>("purchase_order", "Purchase orders", OrdersPermissions.Read)`.

**A list:**

```csharp
var orders = await policies.ApplyAsync(context.Orders.AsNoTracking(), ct);    // IRecordPolicies policies
var page = await orders.ApplyListQuery(query, Fields).ToPageAsync(query.Page, ct);
return ApiResults.List(page.Map(OrderResponse.From), http, query);
```

The page and its total count are counted under the policy. `PolicyQuery<T>` narrows further (`Where`), pages, counts or lists; it never hands the raw query back.

**One record for an action:** `var order = await policies.FindAsync<Order>(id, OrdersActions.Approve, ct);` answers the record, tracked by the module's context so the service can change and save it, or `data.record.not_found` (404) when the reader may not see it, the same as an id that does not exist, or `data.record.forbidden` (403) when the reader may see it but not act. Pass the result's error to `ApiResults.Problem`. The endpoint declares the action's permission with `.ScopedByRow()`; the policy decides per record.

**Testing it:** each block and combination without a database, with a `PolicySubject` over a fake reach (`RecordRulesTests` in the Data tests); the policy against PostgreSQL with records of other units in the table (`RecordPolicyTests`); the architecture tests check the registration.

## Visibility tags on a record type (TF-241, ADR-0057)

Every business record carries visibility tags with no code of its own: `BusinessRecord.VisibilityTags`, a `text[]` of tag codes in the record's own table, empty by default. The policy engine filters every list and read by id by the reader's tags, after the scope rule and the module's policy, in the same query: a reader who holds tags sees a record without tags or one that shares a tag with them; one who holds none sees every record their roles allow. The field history records the tags as any other field. Leave the property as the conventions map it: the model check fails, naming the entity, for a business record whose table has no `text[]` column `visibility_tags` (renamed, converted or mapped to no table), as it does for one without its history. What a module does:

**Let people change the tags.** The action `tag` belongs to the policy, like `share`; give it a permission, usually the module's manage permission:

```csharp
RecordRules.UnitVisibility<Order>(OrdersPermissions.Read,
    (RecordActions.Update, OrdersPermissions.Manage),
    (RecordActions.Share, OrdersPermissions.Manage),
    (RecordActions.Tag, OrdersPermissions.Manage));
```

A module never sets tags itself: `IRecordTags.ChangeAsync(recordType, id, codes, ct)` does, holding rule 5 (a person puts on, or takes off, only tags they hold, unless they hold none; a retired tag is put on nothing). The kernel maps it as `GET` and `PUT /api/v1/record-tags/{type}/{id}`. In the shell, give the record page `tags={{ recordType: "purchase_order", recordId: order.id }}` and the record template shows the tags under the header, with the change for a reader who may.

**Take the master's tags (rule 6), when the product's PRD says so.** In `AddServices`, name the master by its record type and the field that holds its id: `services.AddTagsFromMaster<Order>("supplier", order => order.SupplierId);`. A new order without tags of its own takes, when it is saved, the tags its supplier carries then; later changes of the supplier's tags do not follow. The master may be another module's: it is named, never referenced.

**Testing it.** The filter, rule 5 and rule 6 are the kernel's, tested once on the data tests' sample module (`VisibilityTagsTests`); a module tests that its policy names `tag` and that its own rule 6 declaration gives a new record its master's tags. A running unit marked for testing has sample records (`make sample-records`, ADR-0057), which the smoke's `visibility-tags` step and `shell/e2e/visibility.spec.ts` use.

## Granting access (ADR-0031)

**Roles and scopes.** A role is a set of permissions with a code, a name and the kind of principal it is for. An assignment gives a role to a principal at a scope: the whole tenant, one unit, a unit with the units below it, or one record. Declare new permissions in the module's Contracts and list them in its `Permissions`; the catalogue (`GET /api/v1/permissions`) and the role editor pick them up, grouped by module, with their risk. The seed keeps the system roles in step with the catalogue: `tenant-administrator` holds everything, `read-only` and `service-default` every `*.read`.

**Nobody grants more than they hold.** Through the API, a grant needs `authz.assignments.manage` and every permission of the role over the whole scope; a unit-only assignment does not count for a unit with its children. A tenant role holds only permissions its editor holds tenant-wide, and an import is held to the same rule. The last tenant-wide tenant administrator stays. `IRoleAssignments.AssignAsync` is the seed's trusted path and checks none of this: never expose it to a request. A grant or a removal clears the principal's cached grants on that node at once; elsewhere they expire within the 60-second window (ADR-0012).

**A page that asks for access at a unit.** Navigation and route guards use `session.permissions`, which holds what the principal holds anywhere; the API keeps deciding per row. An action whose scope is a unit asks `/me/access` for that unit through the hook:

```tsx
const access = useAccessAt(unit ? { unit: unit.id } : undefined, ["authz.units.manage"]);
{access.can("authz.units.manage") && <Button onClick={rename}>{t("units.actions.rename")}</Button>}
```

The hook asks only when the principal holds the permission somewhere, and caches the answer for 30 seconds. An action on something that reaches a unit and every unit below it, such as removing an assignment with children, passes `{ unit, includeChildren: true }` and gets only what is held over the whole subtree. An action for a record passes `{ recordType, recordId, unit }`; an action whose scope is the tenant, such as changing a role, reads `session.tenantPermissions`. Show nothing while the answer is on its way, and let the endpoint's refusal (403 with the missing permissions named) land in the page's alert.

**Testing it.** Each rule has an integration test with its error code (`AssignmentServiceTests`, `RoleServiceTests`, `RoleImportTests` in the Authorization tests); a scoped list has a test with a reader at a child unit and a principal outside (`ScopedUsersTests` in the Identity tests); `make e2e` sets up the run's own unit with a child unit inside it and signs in as that unit's administrator (`shell/e2e/scoped.spec.ts`).

## Delegating and reviewing access (ADR-0037)

A **duty** is a set of permissions a person may hand to someone else for a while: approval while on leave, receiving during a trip. A module declares its duties next to its permissions, and lists them in its `IModule`:

```csharp
public static class ReceivingDuties
{
    public static readonly Duty Receiving = new(
        "receiving.goods_in",
        "receiving goods",
        "Booking deliveries in and recording what arrived.",
        [ReceivingPermissions.DeliveriesRead, ReceivingPermissions.DeliveriesBook]);

    public static IReadOnlyList<Duty> All => [Receiving];
}

// in ReceivingModule
public IReadOnlyList<Duty> Duties => ReceivingDuties.All;
```

The code is dotted lowercase words starting with the module's name; the name is the words the delegation dialog shows; the permissions are the module's own or those its pages need. `all` is the kernel's: every permission the person holds. Run `make catalogue`, which lists the duties with their permissions, and commit the catalogue.

What a module gets for nothing:

- **Acting for someone.** While a delegate acts for a delegator, `ICurrentPrincipal.Id` stays the delegate and `ICurrentPrincipal.ActingForId` is the delegator. Every permission check, every list's row filter (`IRecordAccess`, record policies) and the no-escalation rule use the delegator's rights for the delegated duties, at the delegator's scopes. Do not read `ActingForId` to decide access; it is already decided. Read it only to say in words who acts for whom.
- **Both names in the audit.** The audit writer fills `on_behalf_of_id` from the request; an event a module writes names both people without a line of its own.
- **A token never acts.** A service account's or an API client's request is always its own.

A module that must not be done while acting (an action only the person themselves may take) checks `current.ActingForId is not null` and refuses with its own error code; say why in its ADR.

**Access reviews** need nothing from a module: they review role assignments, wherever the roles come from. A reviewer holds `authz.reviews.decide` at a unit; a revoke goes through the assignments' removal path. A business module that knows the reviewers' managers replaces `IReviewEscalation` (Authorization's Contracts) so the daily pass names them; the kernel's default names the campaign's owner.

Tests: `tests/Kernel/Iron.Kernel.Authorization.Tests/DelegationTests.cs` declares a probe module with three duties and shows each refusal and acting with exactly the delegator's rights; `ReviewTests.cs` covers the snapshot, the reviewers' units, the revoke and the guard, and the escalation on a fake clock; `tests/Kernel/Iron.Kernel.Identity.Tests/ActingForTests.cs` follows acting through the session, `/me` and the audit list.

## Working across units (ADR-0038)

A business record belongs to one unit and is **private** to it by default: the people whose roles cover that unit see it. It can be **shared** with listed units, whose people may then read it, or opened to the **tenant**, which everyone holding the read permission somewhere sees. A module writes none of this: `BusinessRecord` carries the mode, the data library keeps the listed units, and `UnitVisibility` and the policy engine follow the mode in the list's own query.

To let people share a record, allow the action `share` in its policy, with the permission over the owning unit that should decide it:

```csharp
services.AddRecordPolicy(
    OrderRecords.OrderType,
    "Sales orders",
    OrderPermissions.OrdersRead,
    RecordRules.UnitVisibility<Order>(
        OrderPermissions.OrdersRead,
        (RecordActions.Update, OrderPermissions.OrdersManage),
        (RecordActions.Share, OrderPermissions.OrdersManage)));
```

The record page then shows the shell's `SharingControl` (`@/components/sharing`) with the record type and id; it reads and changes the sharing through `GET` and `PUT /api/v1/record-sharing/{type}/{id}`, and shows only the words to a reader who may not change it. Changing it is audited with the units before and after.

What a module gets for nothing:

- **The read audit.** A read by id (`FindAsync`) of a private or shared record outside the reader's home unit and the units below it is audited with what allowed it; a refused read of a record that exists is audited as the reader was answered; a list page with such records writes one event. Open records through `IRecordPolicies`, never by a raw query, and this holds.
- **The working unit.** A person whose roles reach several units chooses the unit they work for in the header. Lists keep to it and the units below it; reads by id do not. Give a new record its unit with `NewRecords.OwningUnit(currentPrincipal)`, or `NewRecords.OwningUnit(currentPrincipal, chosen)` when the page lets the person choose; it answers null for a token or a job, which must name the unit. Resolve a setting for the person with `SettingContext.ForPerson(currentPrincipal.Id)` and it follows the unit they work for. Do not read `ICurrentPrincipal.WorkingUnitId` to decide access; read it only for a default.
- **"All my units".** A role at a parent with children, or at the tenant, opens the consolidated view: lists show everything the person may see, new records and settings follow the home unit.

**A merge moves a module's records.** Register the records of a module context once, beside its policies:

```csharp
services.AddUnitRecordOwners<OrdersDbContext>("orders");
```

`ContextRecordOwners<TContext>` finds every business record type of the context with a policy, lists what a unit owns for the preview by the type's code and the display name its policy registration gives (the preview shows the name), and moves it to the surviving unit in one statement per type, deleted records included, in the merge's transaction. A module that keeps something else at a unit (its own per-unit configuration, a counter) implements `IUnitMergeParticipant` from Authorization's Contracts: `PreviewAsync` reads what would move without changing anything, `ApplyAsync` moves it in the caller's transaction and answers what moved, and `MergedAsync` refreshes a cache after the commit. Return one `UnitMergePart` per kind of thing, with its words for the preview, its ids for the audit event, and what is dropped as a conflict. Register it with `services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitMergeParticipant, MyMerge>())`.

**A unit's readiness.** A product that needs something in each unit before people work there implements `IUnitReadiness` (Authorization's Contracts) and answers its checklist: one `ReadinessItem` per need, in words, done or not.

```csharp
public sealed class OrdersReadiness(OrdersDbContext context) : IUnitReadiness
{
    public async Task<IReadOnlyList<ReadinessItem>> CheckAsync(OrganisationUnitInfo unit, CancellationToken cancellationToken)
    {
        var numbered = await context.NumberSeries.AnyAsync(s => s.UnitId == unit.Id, cancellationToken);
        return [new ReadinessItem("orders", "orders.numbering", "A numbering series for orders", numbered)];
    }
}

// in OrdersModule.AddServices
services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitReadiness, OrdersReadiness>());
```

The units page shows the items and whether the unit is ready; the kernel registers none.

Tests: `tests/Kernel/Iron.Kernel.Data.Tests/Policies/SharingTests.cs` covers the modes with paging, the read audit, the refusals, the list audit, the working unit and a merge's move; `tests/Kernel/Iron.Kernel.Authorization.Tests` has a probe module with a real business record (`ProbeRecords.cs`) used by `WorkingUnitTests`, `UnitMergeTests` and `RecordSharingEndpointTests`; `tests/Kernel/Iron.Kernel.Identity.Tests/ScopedUsersTests.cs` follows the working unit and a merge over HTTP; `tests/Kernel/Iron.Kernel.Settings.Tests/UnitChangeTests.cs` proves that a move keeps a unit's settings and a merge moves them.

## Explaining access and conflicts (ADR-0039)

An administrator asks why a person can or cannot do something in the console's **Explain access**, or from a user's Access tab with **Why can't they…?**. The answer lists the checks the request meets, in order, from the same decisions the request makes. A module gets this for nothing for every named endpoint that declares its permission: give the endpoint a name (`WithName`) and a summary in words (`WithSummary`), since the summary is what the person picks the action by, and scope it as usual (`ScopedToUnit`, `ScopedToRecord`, `ScopedByRow`). A record route that has a policy action other than read names it: `ScopedToRecord(type, owningUnit, policyAction: RecordActions.Update)`.

A gate added to the request path must answer its decision without side effects, beside the check that logs and audits:

```csharp
public interface IMyGate
{
    // The decision alone: no log, no metric, no audit. The explanation calls this for the explained person.
    Task<MyDecision> DecideAsync(MyRequest request, CancellationToken cancellationToken);

    // The request's check: the same decision, then the refusal's log, metric and audit.
    Task<MyDecision> CheckAsync(MyRequest request, CancellationToken cancellationToken);
}
```

Then add its check to `AccessExplainer` in its place in the order. A check a handler makes on its own is not explained (an allowance says the action may still refuse for its own reasons), so a handler that refuses for its own reason says why in the error's words.

**Conflict rules** need no code in a module: a rule's sides are its permissions and roles, which the tenant's administrators choose in the console's **Conflict rules**. What a module should know:

- A grant that would complete a blocking rule is refused with `authz.sod.conflict` (409), through the API and through `IRoleAssignments.AssignAsync`, the trusted path a seed or another module uses. A module that grants roles itself handles that error.
- A rule's role side counts only when the grant gives every permission of the role. A change of a role's permissions is checked for everyone who holds it: a blocking rule refuses it with the rule named and the number of people affected. System roles follow the catalogue when the seed runs, unchecked: a new module's permissions in a system role show in the next report.
- Conflicts are by overlapping scope: a tenant grant meets every unit, a unit grant with its children meets its subtree, two unrelated units never meet.
- The report runs in the worker host (`AddConflictReport()`, daily and two minutes after start); a grant, a removal and a delegation refresh that person's rows at once, and a role's change its holders' rows.

Tests: `tests/Kernel/Iron.Kernel.Authorization.Tests/ExplainAccessTests.cs` holds the matrix that compares each explanation with the real call (licensed module, feature, kill switch, scope, acting, record policy), and the audit; `ConflictDetectorTests.cs` covers the overlap rule alone; `ConflictTests.cs` covers each mode on assignments and delegations, unrelated units, the report on a fake clock after a rule's change, and the review marker; `tests/Kernel/Iron.Kernel.Identity.Tests/ScopedUsersTests.cs` shows that a person of another tenant cannot be explained.

## An encrypted field, and one found by its exact value (TF-074, ADR-0054)

A field whose value must not be readable in a dump, a backup or the database's own tools (a bank account, a tax identifier, a secret a person enrolled) is an `EncryptedValue`; one that must also be found by its exact value is marked `[Searchable]` and keeps its keyed hash beside it, named after it with `Hash`:

```csharp
public sealed class Supplier : BusinessRecord, ITrackedChanges
{
    [Searchable]
    public EncryptedValue? TaxIdentifier { get; private set; }

    public SearchHash? TaxIdentifierHash { get; private set; }

    public EncryptedValue? BankAccount { get; private set; }

    public void IdentifyForTax(SearchableValue taxIdentifier)
    {
        TaxIdentifier = taxIdentifier.Value;
        TaxIdentifierHash = taxIdentifier.Hash;
    }

    public void PayTo(EncryptedValue bankAccount) => BankAccount = bankAccount;
}
```

The service encrypts before it saves and opens only what it shows, through `IFieldEncryption`:

```csharp
supplier.IdentifyForTax(await encryption.SealSearchableAsync(request.TaxIdentifier, cancellationToken));
supplier.PayTo(await encryption.EncryptAsync(request.BankAccount, cancellationToken));

var hashes = await encryption.SearchHashesAsync(query.TaxIdentifier, cancellationToken);
var found = await suppliers.Suppliers.Where(s => hashes.Contains(s.TaxIdentifierHash!)).ToListAsync(cancellationToken);
```

A convention stores each as text with its key version; give the hash an index in `ConfigureModule`, then `make ef-add`. The search hashes one value per live version of the index key, so it finds a record during a rotation too; the value is normalised first (no white space, hyphens, dots or slashes, in capitals), so `DE 123-456` finds `de123456`. That is the only search such a field has: not a part, a prefix, a range, a sort or `LIKE`. A field that needs one of those is not encrypted, or keeps a separate, less sensitive form beside it (the last four digits). Open many values at once with the list overload, which is one call to the key service. Nothing else is needed for rotation: the Keys module re-encrypts every `EncryptedValue` and re-hashes every `SearchHash` of every module context after a key's rotation, a batch at a time; a raw SQL write of one of these columns must write what `IFieldEncryption` made. The model check refuses a `[Searchable]` field without its hash; the record's history shows an encrypted field only as changed.

## A record type under retention (TF-066, ADR-0050)

A module that owns records with a life puts each record type into a retention class and lets the platform delete them:

```csharp
public sealed class OrderRetention(OrdersDbContext orders) : IRetainedRecords
{
    public string RecordType => "order";
    public RetentionClass Class => RetentionClasses.BusinessDocument;

    public async Task<IReadOnlyList<RetainedRecord>> ExpiredAsync(DateTimeOffset cutoff, int max, CancellationToken cancellationToken) =>
        await orders.Orders.Where(o => o.ClosedAt < cutoff).OrderBy(o => o.ClosedAt).Take(max)
            .Select(o => new RetainedRecord(o.Id, o.ClosedAt!.Value, "supplier", o.SupplierId, JsonSerializer.Serialize(o)))
            .ToListAsync(cancellationToken);

    public Task DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => ...;
}

services.AddRetainedRecords<OrderRetention>();
```

The retention job archives each expired record's `Content` to the tenant's container, skips any a legal hold keeps (the record, its master such as the supplier, or the whole tenant), calls `DeleteAsync` with the rest, and writes `audit.record_expired` for each. The data tests' `SampleOrderRetention` and Identity's `SessionRetention` are the references. A new class is a decision: an ADR, then `RetentionClasses`.

## A record's history (TF-062, ADR-0051)

Every business record is tracked: mark the entity `ITrackedChanges`, or the model check fails naming it. Mark a field `[Sensitive]` to keep only its last four characters in the history (a bank account), `[Secret]` to keep only that it changed (a key's hash), and `[NotTracked]` for what changes by itself and other events already tell (a counter, a last-seen time). Nothing else is needed: each save writes one `record.changed` event per record with its fields, old and new, in the save's own transaction, and the record's History tab shows it. A public `const string RecordType` on the entity names it in the history, the same name its own events use, so its History shows both; where a property already has that name, `[TrackedAs("legal_hold")]` on the class names it; otherwise its table's name does. To give the change a reason, set `IChangeReason.Reason` before the save. A page with the record template has the History tab (`history` from `useHistoryOf`); a list page gives each record `RecordHistoryButton`, which opens the same History in a dialog.

## Signing a record (TF-063, TF-219, ADR-0051)

A module lets people sign a document type by declaring it and writing its snapshot:

```csharp
public sealed class OrderSignatures(OrdersDbContext orders, IOrderAccess access) : ISignableRecords
{
    public static SignableType Type { get; } = new("order", "Purchase order",
        [SignatureMeanings.Approved, SignatureMeanings.Rejected],
        new SigningPolicy(Required: true, [SignatureMeanings.Approved, SignatureMeanings.Rejected], Reauthenticate: true));

    public IReadOnlyList<SignableType> Types => [Type];

    public static RecordSnapshot Snapshot(Order order) =>
        RecordSnapshot.Of("order", order.Id, order.Version, order.Number, new { order.Number, order.SupplierId, order.Total, Lines = order.Lines.Select(l => new { l.Item, l.Quantity, l.Price }) });

    public async Task<RecordSnapshot?> CurrentAsync(string recordType, Guid id, CancellationToken cancellationToken) =>
        await orders.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken) is { } order ? Snapshot(order) : null;

    public Task<bool> MayReadAsync(string recordType, Guid id, CancellationToken cancellationToken) => access.MayReadAsync(id, cancellationToken);
}

services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignableRecords, OrderSignatures>());
```

The snapshot is every field a person decided on, at the record's version; never a time the database rounds, nor a name that can change elsewhere. Then the module signs inside its own action, the signer proved before the transaction so a wrong password counts whatever happens next:

```csharp
var policy = await signatures.PolicyAsync("order", cancellationToken);
SignerProof? proof = null;
if (policy.Required)
{
    var proved = await signatures.AuthenticateAsync("order", request.Signature?.ToCredentials() ?? SigningCredentials.None, cancellationToken);
    if (proved.IsFailure) return proved.Error;
    proof = proved.Value;
}

await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
order.Approve();
await orders.SaveChangesAsync(cancellationToken);
if (proof is not null)
{
    var signed = await signatures.RecordAsync(proof, OrderSignatures.Snapshot(order), SignatureMeanings.Approved, request.Comment, cancellationToken);
    if (signed.IsFailure) return signed.Error;
}

await transaction.CommitAsync(cancellationToken);
```

Identity decides what the credentials must be (the first signature of a session asks for the login identifier, the password and the second factor; later ones for the password), and refuses impersonation, acting for someone, tokens and service accounts; the module never reads a credential. The shell's `SigningDialog` asks for what `GET /api/v1/signatures/challenge?record_type=` says, and the record template's `SignatureBlock` shows a record's signatures, on screen and in print. Give the dialog `context`, the page's own description of what is signed as plain data: a company-login person leaves for the provider's fresh sign-in, and when they come back, `useResumedSigning(recordType)` hands the page that context with the meaning and the comment, so it reopens the dialog ready to confirm. Access review decisions (`AccessReviewSignatures`) and audit-trail reviews (`AuditTrailReviews`) are the references; the Compliance tests' sample document is a business record signed and changed.

## A save that waits for a second administrator (TF-068, ADR-0052)

A save of sensitive configuration asks the kernel's `IFourEyes` after its own checks and before it writes. The retention choice is the pattern:

```csharp
// After validation, escalation, conflict rules and step-up, exactly as a direct save checks them.
if (fourEyes is not null && await fourEyes.AppliesAsync(ConfigurationAreas.Retention, cancellationToken))
{
    var proposal = new ChangeProposal(
        ConfigurationAreas.Retention,
        ChooseKind,                                      // "audit.retention.choose": the applier's key
        $"retention:{code}",                             // the subject: one pending change per subject
        $"Keep {retention.Description.ToLowerInvariant()} for {days} days",
        await StateAsync(code, cancellationToken),       // before, as people read it
        ChangeJson.Of(new { Class = code, Days = days, Chosen = true }),   // after, the same shape
        ChangeJson.Of(new RetentionChangePayload(code, days)),            // what ApplyAsync needs
        AuditPermissions.RetentionManage.Name,           // what the approver must hold
        AccessScope.Tenant,                              // and where
        reason);
    return await fourEyes.HoldAsync(proposal, cancellationToken);   // an Error of kind Held: the API answers 202
}
```

Then an `IConfigurationChangeApplier` (registered with `TryAddEnumerable`) answers for the module's kinds: `Handles(kind)`, `CurrentAsync` (the subject's state now, the same shape as the before, which Changes compares before applying), `ApplyAsync` (the same service method a direct save calls; inside it `AppliesAsync` answers false, so it writes), `DiscardAsync` when the module keeps something aside (a single sign-on secret staged in the store), and `AppliedAsync` after the approval's transaction committed, for a module that reads what it wrote back into a cache (the settings do). Take `IFourEyes?` with a null default, so a host without the Changes module still builds the service. A setting joins an area where it is declared: `.GovernedBy(ConfigurationAreas.MfaAndPasswordPolicies)`. The shell needs nothing: `request()` throws the held answer as an `ApiError` with `isHeld`, the form says it waits, and the frame links to the change.

## Contributing to an evidence pack (TF-067, ADR-0052)

A module adds its tables to a pack's documents with an `IEvidenceSource` (Compliance's contracts), registered with `TryAddEnumerable`: `TablesAsync(period)` answers its `EvidenceTable`s, each naming its document (an `EvidenceDocuments` code), a name for the CSV, a title, the columns, the rows as text, and a note when something is not covered. A state "at the period's end" is rebuilt from the field history: `TrackedFields.Of(context, entities)` gives the records' tracked fields now, and `IRecordHistory.AsOfAsync(recordType, current, period.To)` undoes every save after it (`RecordStates.Rewind`). Never a secret: a client secret, a key or a password hash is not evidence. A new event type an administrator causes is marked `.AsAdministrative()` beside its constant, and the administrative actions list it.

## Licensing a module or feature (ADR-0034)

Kernel modules are the platform: always on, never in a plan, and they declare no licence (the architecture test `LicensingRules` refuses one). A Business or suite module must declare one. Features may be licensed in any layer.

1. **Declare it in the module's own code.** The licence code and the subscription type in the module's `IModule`, the features in its Contracts beside its permissions, listed in its `IModule`:

   ```csharp
   public static class SourcingFeatures
   {
       public static readonly Feature Auctions = new("sourcing.auctions", "Auctions with pre-bidding");

       public static IReadOnlyList<Feature> All => [Auctions];
   }

   public sealed class SourcingModule : IModule
   {
       public ModuleLicence Licence => new("sourcing", SubscriptionType.PerPerson);

       public IReadOnlyList<Feature> Features => SourcingFeatures.All;
       // Name, Permissions, AddServices, MapEndpoints as for any module
   }
   ```

   A licence code is lowercase letters, digits and underscores; a feature code is `area.feature`.
2. **Nothing else for the module's endpoints.** `MapModules` puts every endpoint of a licensed module behind the check: 403 `entitlements.module_disabled` when the tenant does not have it, `entitlements.read_only` for a write when it is read only, `entitlements.seat_required` for a person without a seat of a per-person module. The check runs after authentication and before the permission.
3. **A feature on its endpoints:** `.RequirePermission(...).RequireFeature(SourcingFeatures.Auctions.Code)`; 403 `entitlements.feature_disabled` when it is not available, which includes a feature whose module is not. Code that needs to ask (a background job, a choice inside a handler) injects `IEntitlements` from `Iron.Kernel.Entitlements.Contracts`: `Module(licence)`, `ModuleForAsync(licence, principal)`, `Feature(code)`.
4. **Write the catalogue:** add the module to `Iron.Host.Api.UnitModules` (a suite's module to its suite's definition, which `UnitModules.Suites()` carries), then `make catalogue`, and commit `src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json`. The host test fails until the file matches the modules; the control plane refuses a plan with a code the file does not have.
5. **In the shell:** `licence: "sourcing"` in the module's manifest hides its navigation, routes, opener and quick actions until `/me/context` says it is entitled; `useFeature("sourcing.auctions")` answers `available` (show the control) and `writable` (show what changes data). The shell hides; the API decides.
6. **Put it in a plan:** `PUT /api/v1/plans/{code}` on the control plane, then `make apply-plan TENANT=x PLAN=y`. A plan belongs to the Foundation or to one suite and names only that group's modules and features (ADR-0059); a suite's starter plans are declared beside the suite and seeded from the catalogue. A per-person module gets its seat count there; seats are assigned in the unit (`POST /api/v1/seats`).

Tests: a module's tests prove its own rules with an allow-all gate in the test host (`AllEntitled`, as the module test projects have); `tests/Kernel/Iron.Kernel.Entitlements.Tests` proves the check with a fixture module licensed per person.

## Feature flags and kill switches (ADR-0035)

A flag changes behaviour without a deploy: a release turned on for some tenants first, an experiment, an operational switch. It is not an entitlement: what a tenant has paid for is licensed (the section above), never flagged.

1. **Declare it in the module's own code**, in its Contracts, and list it in its `IModule`:

   ```csharp
   public static class SourcingFlags
   {
       public const string Owner = "Sourcing team";

       public static readonly Flag NewAwardScreen = Flag.Boolean(
           "sourcing.new_award_screen", FlagKind.Release, "Shows the new award screen while it is proven.", Owner,
           removeBy: new DateOnly(2027, 3, 31), defaultValue: false);

       public static IReadOnlyList<Flag> All => [NewAwardScreen];
   }

   // in SourcingModule
   public IReadOnlyList<Flag> Flags => SourcingFlags.All;
   ```

   A key is `area.flag` in lowercase with underscores. The type is boolean, string, number or JSON (`Flag.String`, `Flag.Number`, `Flag.Json`). An owner and a purpose are required; release and experiment flags need the date they are to be removed by. The default is what the flag answers when no rule matches, and the only answer while the unit has never known any rules.
2. **Write the catalogue:** `make catalogue`, and commit `licensing-catalogue.json`. The control plane refuses rules for a flag it does not have.
3. **Ask it in code** through OpenFeature: inject `IFlagClients` from `Iron.Kernel.Flags.Contracts`, then `var flags = await clients.CurrentAsync(ct);` (the signed-in principal) or `ForAsync(principalId, ct)` (a job acting for someone), and `await flags.GetBooleanValueAsync(SourcingFlags.NewAwardScreen.Key, false)`. The caller's default answers an undeclared flag or a wrong type.
4. **Or on an endpoint:** `.RequireFlag(SourcingFlags.NewAwardScreen.Key)` answers 404 `flags.disabled` while the flag is off for the caller. A risky path gets its kill switch with `.RequireFlagOff(KillSwitches.Imports.Key)`, which answers 503 `flags.killed` with the operator's reason while it is on. Both run after the entitlement check and before the permission, and are audited.
5. **In the shell:** `useFlag("sourcing.new_award_screen", false)` from `@/auth/flags`. The value comes from `/me/context`, evaluated by the API for this person, and is read again every 30 seconds; the default answers until it comes, and whenever it fails. The type of the default is the type of the answer.
6. **Set its rules** in the control plane: `make flag KEY=sourcing.new_award_screen VALUE=true TENANT=echo` for one tenant, or `PUT /api/v1/flags/{key}/rules` for scopes, percentages and attributes. Units learn a change within 30 seconds, the shell within a minute.
7. **Retire it:** remove the declaration and the code that asks it, and mark the flag `"status": "retired"` in `licensing-catalogue.json` in the same change. `make catalogue` and the host test refuse, naming the flag and its owner, a flag that left the code while the catalogue marks it active, and a retired flag still declared. The control plane's flag report lists flags past their removal date with their owner.

**The shell never decides security by a flag.** A flag in the shell shows or hides; it never stands in for a permission, an entitlement or a check the API makes. The values reach the browser, and a person can change what their browser does. Every action a flag hides is checked again by the API: the permission, the entitlement and, for a kill switch, `RequireFlagOff`.

**Kill switches** are a reserved family, `kill.<path>`, declared by the kernel only: email, webhooks, sms, ai, push and imports. A module never declares one; the path that needs a switch names it on its endpoints or asks it in its jobs. Only an operator turns one on or off, with a reason (`make kill-switch`, runbook section 5). While one is on, administrators see a banner with the path, since when and the reason.

Tests: test hosts register an allow-all gate beside the entitlements one (`AllEntitled` implements `IFlagGate` too); `tests/Kernel/Iron.Kernel.Flags.Tests` proves the evaluation order, the refresh and the kill switch on a real store.

## Declaring a setting (ADR-0036)

A setting is something the tenant decides: a limit, a validity, a template, a preference. It is not a flag (the platform's switch, the operator's) and not an entitlement (what the tenant has paid for).

1. **Declare it in the module's Contracts** as a typed static definition, and list it in its `IModule`:

   ```csharp
   public static class SourcingSettings
   {
       public static readonly Setting<int> QuoteValidDays = Setting.WholeNumber(
           "sourcing.quote.valid_days", "How long a supplier's quote stays valid, in days.", 30, 1, 365, SettingScopes.Tenant | SettingScopes.Unit);

       public static IReadOnlyList<SettingDefinition> All => [QuoteValidDays];
   }

   // in SourcingModule
   public IReadOnlyList<SettingDefinition> Settings => SourcingSettings.All;
   ```

   The factories are `Text` (with a `SettingRule.Length` or `SettingRule.Matches` and words), `WholeNumber` and `Number` (with their range), `Boolean`, `Choice` (with the words it allows), `Json` (with a `SettingRule.Must` and words), and `Secret`. The scopes say where a value may be set: the tenant, a unit (and the units below it), a person. `visibleToShell: true` sends the effective value to the shell; never for a secret.
2. **Write the catalogue:** `make catalogue`, and commit `licensing-catalogue.json` and `shell/src/api/setting-keys.ts`. The host test fails until both match the code.
3. **Read it** through `ISettings` from `Iron.Kernel.Settings.Contracts`, for the context the value is for:
   - `SettingContext.ForPerson(principalId)`: the person's own value, then their home unit walking up, then the tenant;
   - `SettingContext.ForUnit(record.OwningOrganisationId)`: for a record, its unit walking up, then the tenant (TF-221);
   - `SettingContext.Tenant`: for what belongs to nobody in particular.

   `await settings.GetAsync(SourcingSettings.QuoteValidDays, context, ct)` answers an `int`. There is no string overload: a key nobody declared does not compile. A change is seen by every process within 30 seconds, without a restart.
4. **A secret** is a `Setting.Secret(...)`: its value is a path under `tenants/<slug>/settings/`, never the secret. Read the secret with `ISecretSettings.ReadSecretAsync(setting, context, ct)` where you use it, and do not keep it: the service keeps it five minutes at most, and nothing else may hold it longer, log it or return it.
5. **In the shell:** `useSetting("sourcing.quote.valid_days")` from `@/auth/settings` answers the effective value for the signed-in person, typed by the generated keys, and the default until `/me/context` answers. Give the setting a name in `src/i18n/en.json` under `settings.names` (the key with its dots as underscores), and, for a number, its amount under `settings.amounts` with `_one` and `_other`; the console's Settings page shows it then by its module.

The console's page and the API check the permission at the place changed (`settings.values.manage` tenant-wide or at the unit); a module adds nothing for that. Tests: `tests/Kernel/Iron.Kernel.Settings.Tests` declares a probe module with a setting for every scope and a secret reference; a module's own tests read its settings through a host that lists the Settings module, as Identity's do.

## The control plane and a provisioning step (ADR-0017 to ADR-0019)

The control plane is a unit like the others, with `Iron.Kernel.Tenancy` (the registry and the tenant API) and `Iron.Kernel.Fleet` (jobs, steps, the worker). A new thing to do for every tenant is a new step:

1. Add a class in `src/Kernel/Iron.Kernel.Fleet/Services/Steps/` implementing `IProvisioningStep`: a `Name`, an `Order`, the `Kinds` of job it belongs to (`Provision`, `Update`, `Deprovision`), and `RunAsync(ProvisioningContext, ...)` returning what it reports.
2. Make it idempotent and resumable: read what an earlier run left in the store or the database before creating anything, and keep nothing generated only in memory (ADR-0018). A step that talks to the unit does it through `IUnitRuntime`, never through Docker directly.
3. Register it in `FleetModule.AddServices` and extend the step list in `ProvisioningTests`; the tests run the steps against real PostgreSQL, Azurite and OpenBao containers with a recording runtime.

The fleet worker needs nothing else: it runs the steps of the job's kind in order, records each with its duration, stops at the first failure, and continues from it on the next request.

## A shell page (ADR-0020, ADR-0021, ADR-0027)

1. Add the API binding in `shell/src/api/<module>.ts`: a Zod schema for the response and a function that calls `request()` with it. For a list, use `listSchema(ItemSchema)` and take the ListQuery string as the parameter.
2. Put the page on a template from `shell/src/components/templates`: `ListPage` for a list (the title, one line, the actions, the filters, and the grid; the density switch comes with it), `RecordPage` for a record (the `RecordHeader` with its status as a word, then the panels behind tabs: `AttachmentPanel`, `HistoryPanel`, `ApprovalPanel`, `CommentPanel`), `WizardPage` for steps, `ConsolePage` for settings, `DashboardPage` for numbers. Nothing here is `EmptyState`; a wait is `LoadingState`; a question is `ConfirmDialog`; a form in a dialog is `FormDialog`. They live in `shell/src/components/panels`.
3. For a list, declare the columns (`field` is the API's field name; `sortable` and `filter` follow the API's field table) and render `<DataGrid columns={...} fetchPage={...} defaultSort={...} label={...} onRowOpen={...} />`; paging, sorting and filtering are server round trips the wrapper builds. Space and Enter select the focused row, a double click or Ctrl+Enter runs `onRowOpen`, and `onSelectionChange` tells the page what is selected.
4. For a form, `useZodForm({ schema, initialValues })` and `<Form form={form} onSubmit={save} submitLabel={...} secondary={...}>` with Mantine inputs bound through `form.getInputProps`. Field names are camelCase; when `save` rejects with an `ApiError`, the API's field problems land on their fields (`items[0].unit_price` becomes `items.0.unitPrice`) and the rest shows at the top with the correlation id. Never two primary actions.
5. Add the route and the navigation entry to the module's manifest in `shell/src/modules/<layer>/<module>/manifest.tsx`, each with its `permission` (`null` for any signed-in principal), and the manifest to `shell/src/modules/index.ts` if it is new. Every string goes through `src/i18n/en.json`.
6. `make shell-check`: Prettier's check fails unformatted code (`make format` fixes it); a route without a permission fails the strict validation; an `ag-grid-*` import outside the wrapper and a colour by value fail lint. A new component gets its tile in the gallery, and `make shell-a11y` scans it.

Every time a page shows goes through `shell/src/i18n/dates.ts` (TF-065 AC2, ADR-0050): `formatDateTime` for a moment, with the person's zone named; `formatTime` for a moment today; `formatDate` for a moment's day; `formatDay` for a calendar day that has no time of its own (a due date, a day of a UTC summary), shown as that day in every zone; `startOfDay` for the first instant of a day the person typed, in their zone, for a search. Never `toLocaleString` or `Intl.DateTimeFormat` in a page. The zone is the person's `shell.time_zone`, or the browser's.

A record page gives the template its record as the audit trail names it, `history={useHistoryOf("principal", user.id)}` from `@/auth/record-history`, and gets the History tab for people who may read the trail.

A page reads the signed-in principal with `useSession()`; it never checks a permission itself beyond what the manifest declares, since the API's answer is the one that counts. Errors from `request()` are `ApiError` with the correlation id; the error views show it.

A page's tests stay fast on a loaded machine (A7; `make shell-check` passes beside the running stack):

- Put text into a field with `fill(user, field, text)` from `@/test/render`, one paste, not `user.type`, which renders the page again for every key. Keep `user.type` for a test about keys.
- Render what the test is about with `renderWithProviders`, not the whole `Layout`, unless the test is about where something sits in the frame.
- A timer the page sets (a session clock, a 30-second refresh) is advanced with `vi.useFakeTimers({ shouldAdvanceTime: true })` and `vi.advanceTimersByTimeAsync`, never waited for in real time, and `vi.useRealTimers()` in `afterEach`.
- When the order of two answers matters, hold one in the stub until the test has seen the first, rather than racing them.
- An explicit timeout only where a test renders something heavy: the grid, the gallery, the lint test. Vitest's workers are bounded in `vite.config.ts` (half the cores, at most 8).

## Styling a page (ADR-0024, ADR-0025, ADR-0040)

A page uses the components as the theme styles them and names colours by meaning: `color="danger"` on an alert, `c="dimmed"` on a hint, a `var(--iron-...)` variable from `shell/src/design/tokens.ts` when a style needs a value. A hex, `rgb()`, `hsl()`, a CSS colour name or a Mantine palette name (`red`, `gray.6`) in a `style`, `styles`, `vars` or `gradient` object or in `c`, `bg`, `bd`, `color`, `fill` or `stroke` fails lint, and the message says what to use. The title of a moment (the login page, a home page, an opener) says `<Title order={2} variant="display">` to take the display family; the work area keeps the reading family. The gallery (`make shell-dev`, then http://localhost:5173/gallery, with `?scheme=dark` and `?density=compact` to switch) shows every token and every themed component in both schemes and both densities; a new component or token gets its example there, and `make shell-a11y` runs the WCAG 2.1 AA scan on it and leaves a screenshot of each run in `shell/test-results/a11y/`. Prettier formats the code (`make format`, ADR-0040): the line is 120 characters, so diffs read side by side; let it wrap. The density setting (`useDensity`) and the reduced-motion hook (`usePrefersReducedMotion`) live in `shell/src/design/`. A drawing is a component from `shell/src/design/illustrations/` (`<Welcome decorative size="md" />`): `sm` in a grid or a loader, `md` on a page, `lg` in the gallery; it is hidden from assistive technology when the text beside it says the same, labelled otherwise. The tenant's mark is `<TenantMark hostname={window.location.hostname} size={24} />` from `shell/src/design/patterns/`. A new drawing follows `docs/design/illustration-checklist.md` and gets its tile in the gallery.

## A module on the home page and in the header (ADR-0033)

The home page, the search and the help panel read the manifests, so a module appears in them without a change to the shell.

1. **The opener.** `opener: { accent: "identity", lineKey: "modules.identity.line" }` in the manifest. The accent is one of the accents in `shell/src/design/tokens.ts` (a new module gets its own there, with its meaning in the catalogue); the line says in one sentence what the module is for. The mark is drawn for the module: the mandala generator with the module's name as the seed and one set of parameters for every module (`<ModuleMark name="identity" accent="identity" size={56} />`, the same mark beside the title of its first page). A spot drawing of the module's own goes in `drawing`: a component in `shell/src/design/illustrations/` in the brief's hand, through `docs/design/illustration-checklist.md`. `entries` says how many of its navigation entries the opener lists, three by default. The principal sees the openers of the modules it may open and, in each, only the entries it may open.
2. **Quick actions.** `quickActions: [{ id: "invite", labelKey: "users.invite.open", permission: "identity.users.manage", path: "/users?invite=1", icon }]`. The home page and the search offer the action when `/me/access` answers the permission at the principal's home unit. The page at `path` opens its dialog from the query and then removes the query, as `UsersPage` and `UnitsPage` do, so the home page, the search and a link start it the same way. A quick action without a permission fails the strict validation, as a route without one does.
3. **Help keys.** A route's `shortcuts` list its keys in the help panel on that page: `gridShortcuts`, `treeShortcuts` and `tabShortcuts` from `shell/src/components/shortcuts.ts`, or keys of its own with their words in the catalogue.
4. **Recently opened** names a page by its route's title; a record page names itself with `useRecentName(record.name)` once the record has loaded.
5. **The session warning** needs nothing from a page: every answer through `request()` counts as activity and moves the idle end in every tab. A page that holds unsaved work should still save as it goes; the warning gives two minutes.
6. **A high-risk action** needs nothing from a page either (ADR-0047): when the API refuses it for an old sign-in, `request()` waits while the shared re-authentication dialog asks the person to confirm it is them, then sends it again. If they cancel, the call rejects with the `ApiError` (`needsReauthentication`), which the page shows as any refusal.

## Proving a change (ADR-0022)

| Level | Command | What it proves |
|---|---|---|
| Unit and integration tests | `make test`, `make shell-check` | the code, on real platform services in containers |
| Accessibility | `make shell-a11y` | the gallery against WCAG 2.1 AA with axe in a real browser; no stack needed |
| Smoke | `make smoke` | the live stack from the outside in 26 steps: provisioning, tokens, cookies, denials, the trace, a role scoped to a unit, visibility tags on sample records, a plan change and a kill switch, the operator's recovery, the second factor, single sign-on through the test provider, sessions and revocation, the audit trail, TLS, a key's rotation, a security alert in the tenant, the control plane and Alertmanager, and the tenant's suites |
| Browser | `make e2e` | the user paths against the stack: sign-in with a password, a passkey and the company login, home and the search, the grid, inviting a person, scoped administration, delegation and reviews, settings, working across units, explaining access, a plan and a kill switch reaching the shell, passwords, terms, impersonation, signatures, pending changes and evidence, the protection of the sign-in, keys, security alerts, visibility tags, and a suite's About page from the home page |
| Pipeline's stage | `make integration` | platform and control plane from images, then the smoke; in the release workflow the browser tests follow against the same tenant (ADR-0044) |
| Traceability | `make trace` | each requirement criterion and decision with the tests that prove it and their last results, and the criteria of the done slices that nothing proves (ADR-0044) |
| Security | `make security`, `make coverage`, `make images-security` | the dependencies with their licences, the misconfigurations and the repository's rules through the gate; the coverage floors; the laptop's images and their bills of materials (ADR-0055) |
| Every check in order | `make verify` | `make prd-check`, `make test` with coverage, `make shell-check`, `make shell-a11y`, `make format-check`, `make security`, `make coverage` and `make trace`, one after another; the check before a push, with the stack running or not |

Every pull request runs the first two levels, the security checks, both formatters and the matrix in CI; a merge to main then runs only its build. Each night when main has changed, and on demand, the release builds and scans the images, runs the pipeline's stage on the runner, and keeps the matrix, the validation pack and the bills of materials with them (ADR-0055).

**The tests' platform services.** `make test` starts one PostgreSQL, one Azurite and one OpenBao for the whole run (`tests/test-services.sh`) and runs four test projects at a time; each project works in a database, blob containers and a store mount of its own. A fixture gets them from `tests/Iron.Testing`: `await TestPostgres.StartAsync("orders", ct)` answers a connection string to a new database (and `CreateDatabaseAsync` another, for a second tenant), `TestBlob.StartAsync` the account, `TestOpenBao.StartAsync("orders", ct)` the store with a mount named after the project. A project run alone (an IDE, `dotnet test` on one project) starts containers of its own the same way. Names that live in a whole server rather than a database (PostgreSQL roles, blob containers, store policies) must stay apart between projects: give them the project's name.

**The browser tests' data.** `make e2e` makes a unit of the run's own under the root (`infra/compose/scripts/e2e-data.py`), with the people, units and assignments the tests need inside it, and deactivates it at the end whatever the result. A test that needs a unit makes it under the run's unit (`E2E_RUN_UNIT_ID`), never under the root, and names what it makes with the run's id. A change to a host or a module reaches a laptop tenant with `make images` and `make update-unit TENANT=<slug>`.

## Naming what a test proves (ADR-0044)

Every test names the requirement criteria or the decisions it proves, so that `make trace` can show each criterion with its tests and their results (TF-218). Read the test against the criterion's text: a test proves `TF-033 AC2` when it sets up the Given, does the When and checks the Then, at whatever level decides the outcome.

| What the test proves | You write |
|---|---|
| a criterion | `TF-033 AC2`, or `REQ-012 AC1` for a suite's requirement |
| every criterion of a requirement | `TF-033` |
| a rule an ADR decided that no criterion states (a rule of the build or the architecture, a mechanism's behaviour) | `ADR-0029` |
| nothing a requirement or an ADR states | untraced, with the reason; the design session decides it, so never tag a test to fit |

| Kind | The form |
|---|---|
| .NET | `[Proves("TF-033 AC1", "TF-033 AC3")]` on the line below `[Fact]` or `[Theory]` (namespace `Iron.Testing`, a global using in every test project); `[Untraced("why")]` alone. The build refuses a test method without one (`IRON0005`). |
| Vitest and Playwright | the title ends with the list: `it("a child-unit administrator sees only its users [TF-033 AC1]", ...)`, also at the end of a template, `` test(`... in the ${scheme} scheme [TF-172 AC1]`, ...) ``; `[untraced]` alone, with `// Untraced: why` on the line above. Lint refuses a title without it. |
| The smoke | `await report.StepAsync("login", ["TF-011 AC1"], async () => ...)`; the step's line and the report show it. |
| The Python tools' tests | the docstring ends with the list: `"""A moved identifier is never held again. [ADR-0043]"""`. |

Run the tests of one requirement or criterion with the trait: `dotnet test --project tests/Kernel/Iron.Kernel.Authorization.Tests --filter "proves=TF-033"`, or `"proves=TF-033 AC2"`.

**The matrix.** `make trace` reads every test and what it names from the sources, and each test's result from the last run's files under `artifacts/test-results` (`make test`, `make shell-check`, `make shell-a11y`, `make e2e`, `make smoke` and `make prd-check` write them), and writes `artifacts/trace/matrix.md` and `matrix.json`. A kind that has not run shows its tests as not run. It fails when a test names an identifier no document holds, when a criterion of a done slice (every task of its plan done) has no test, unless `docs/trace/exceptions.md` lists it with its reason and the date the design session approved it, and when an exception is no longer needed. CI publishes the matrix on every run; the release keeps it with its images.

**When a criterion of a done slice loses its last test**, write the test again or ask the design session for an exception; an exception that a new test makes unneeded is removed in the same change.
