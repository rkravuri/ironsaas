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
- Lists: declare the fields once in a `ListFields<T>` (snake_case names, pointing at the entity properties, with a default sort), parse with `ListQueryParser.Parse(http.Request.Query, fields)`, apply with `.ApplyListQuery(query, fields).ToPageAsync(query.Page, ct)` and return `ApiResults.List(page, http, query)`. That works for an entity marked `IOwnAccessRule` (its module applies its own access rule; `ITenantWide` is one); a business record pages through its policy (see Record policies).
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

## Licensing a module or feature (A4, ADR-0034)

Kernel modules are the platform: always on, never in a plan, and they declare no licence (the architecture test `LicensingRules` refuses one). A Business or Products module must declare one. Features may be licensed in any layer.

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
4. **Write the catalogue:** add the module to `Iron.Host.Api.UnitModules`, then `make catalogue`, and commit `src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json`. The host test fails until the file matches the modules; the control plane refuses a plan with a code the file does not have.
5. **In the shell:** `licence: "sourcing"` in the module's manifest hides its navigation, routes, opener and quick actions until `/me/context` says it is entitled; `useFeature("sourcing.auctions")` answers `available` (show the control) and `writable` (show what changes data). The shell hides; the API decides.
6. **Put it in a plan:** `PUT /api/v1/plans/{code}` on the control plane, then `make apply-plan TENANT=x PLAN=y`. A per-person module gets its seat count there; seats are assigned in the unit (`POST /api/v1/seats`).

Tests: a module's tests prove its own rules with an allow-all gate in the test host (`AllEntitled`, as the module test projects have); `tests/Kernel/Iron.Kernel.Entitlements.Tests` proves the check with a fixture module licensed per person.

## Feature flags and kill switches (A5, ADR-0035)

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

## Declaring a setting (A6, ADR-0036)

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

## Delegating and reviewing access (A7, ADR-0037)

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

## Working across units (A8, ADR-0038)

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

## Scoping an endpoint to a record (A1, ADR-0030)

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

## Granting access (A2, ADR-0031)

**Roles and scopes.** A role is a set of permissions with a code, a name and the kind of principal it is for. An assignment gives a role to a principal at a scope: the whole tenant, one unit, a unit with the units below it, or one record. Declare new permissions in the module's Contracts and list them in its `Permissions`; the catalogue (`GET /api/v1/permissions`) and the role editor pick them up, grouped by module, with their risk. The seed keeps the system roles in step with the catalogue: `tenant-administrator` holds everything, `read-only` and `service-default` every `*.read`.

**Nobody grants more than they hold.** Through the API, a grant needs `authz.assignments.manage` and every permission of the role over the whole scope; a unit-only assignment does not count for a unit with its children. A tenant role holds only permissions its editor holds tenant-wide, and an import is held to the same rule. The last tenant-wide tenant administrator stays. `IRoleAssignments.AssignAsync` is the seed's trusted path and checks none of this: never expose it to a request. A grant or a removal clears the principal's cached grants on that node at once; elsewhere they expire within the 60-second window (ADR-0012).

**A page that asks for access at a unit.** Navigation and route guards use `session.permissions`, which holds what the principal holds anywhere; the API keeps deciding per row. An action whose scope is a unit asks `/me/access` for that unit through the hook:

```tsx
const access = useAccessAt(unit ? { unit: unit.id } : undefined, ["authz.units.manage"]);
{access.can("authz.units.manage") && <Button onClick={rename}>{t("units.actions.rename")}</Button>}
```

The hook asks only when the principal holds the permission somewhere, and caches the answer for 30 seconds. An action on something that reaches a unit and every unit below it, such as removing an assignment with children, passes `{ unit, includeChildren: true }` and gets only what is held over the whole subtree. An action for a record passes `{ recordType, recordId, unit }`; an action whose scope is the tenant, such as changing a role, reads `session.tenantPermissions`. Show nothing while the answer is on its way, and let the endpoint's refusal (403 with the missing permissions named) land in the page's alert.

**Testing it.** Each rule has an integration test with its error code (`AssignmentServiceTests`, `RoleServiceTests`, `RoleImportTests` in the Authorization tests); a scoped list has a test with a reader at a child unit and a principal outside (`ScopedUsersTests` in the Identity tests); `make e2e` sets up a child unit on the tenant and signs in as its administrator (`shell/e2e/scoped.spec.ts`).

## Record policies (A3, ADR-0032)

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

## A table and its migration (T8)

1. Add or change the entity in the module's `Data/` folder (derive from `AuditedEntity`, `AppendOnlyEntity` or `BusinessRecord`) and map it in `ConfigureModule`.
2. Generate the migration: `make ef-add MODULE=Authorization NAME=AddRoleColour`. The first time, `dotnet tool restore` installs `dotnet-ef` from `.config/dotnet-tools.json`. `MODULE` is the last segment of the module's name, whatever its layer. The generated files land in the module's `Data/Migrations` (for example `src/Kernel/Iron.Kernel.Authorization/Data/Migrations`); read the `Up` method once, run `make format` (Entity Framework writes a byte-order mark that the format check refuses), then commit them with the entity change.
3. `make test`: the module's `ModelTests` passes only when the migration matches the model; the schema check runs over the migrated schema.
4. Apply it: `make migrate` on the laptop; the fleet runs the image's `migrate` command for every tenant unit.

Migrations are never written by hand and never edited after they are committed; a correction is a new migration. The one change made by hand is a rename, before the first commit: `make ef-add` cannot tell a rename from a drop and an add, so turn its drop and add into `RenameColumn` and say so in the migration's comment (ADR-0031 records the first, `RoleCodeAndName`).


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

`ICurrentPrincipal` is the signed-in principal of the request (from the `iron.session` cookie), nobody when there is no valid session, and the system principal in jobs and commands. `ActingForId` is the person a shell session acts for by delegation (ADR-0037), and null otherwise. Endpoints declare one of three things: `.RequirePermission(x)`, `.RequireSignedIn()` (any signed-in principal: me, logout) or `.AllowAnonymous()` (health, login, invitation acceptance). The filter answers 401 `auth.not_authenticated` for nobody, 401 `auth.session_expired` for a cookie whose session ran out, and 403 `authz.permission_denied` for the wrong somebody.

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

A page's tests stay fast on a loaded machine (A7; `make shell-check` passes beside the running stack):

- Put text into a field with `fill(user, field, text)` from `@/test/render`, one paste, not `user.type`, which renders the page again for every key. Keep `user.type` for a test about keys.
- Render what the test is about with `renderWithProviders`, not the whole `Layout`, unless the test is about where something sits in the frame.
- A timer the page sets (a session clock, a 30-second refresh) is advanced with `vi.useFakeTimers({ shouldAdvanceTime: true })` and `vi.advanceTimersByTimeAsync`, never waited for in real time, and `vi.useRealTimers()` in `afterEach`.
- When the order of two answers matters, hold one in the stub until the test has seen the first, rather than racing them.
- An explicit timeout only where a test renders something heavy: the grid, the gallery, the lint test. Vitest's workers are bounded in `vite.config.ts` (half the cores, at most 8).

## Styling a page (D1, ADR-0024, ADR-0025)

A page uses the components as the theme styles them and names colours by meaning: `color="danger"` on an alert, `c="dimmed"` on a hint, a `var(--iron-...)` variable from `shell/src/design/tokens.ts` when a style needs a value. A hex, `rgb()`, `hsl()`, a CSS colour name or a Mantine palette name (`red`, `gray.6`) in a `style`, `styles`, `vars` or `gradient` object or in `c`, `bg`, `bd`, `color`, `fill` or `stroke` fails lint, and the message says what to use. The title of a moment (the login page, a home page, an opener) says `<Title order={2} variant="display">` to take the display family; the work area keeps the reading family. The gallery (`make shell-dev`, then http://localhost:5173/gallery, with `?scheme=dark` and `?density=compact` to switch) shows every token and every themed component in both schemes and both densities; a new component or token gets its example there, and `make shell-a11y` runs the WCAG 2.1 AA scan on it and leaves a screenshot of each run in `shell/test-results/`. The density setting (`useDensity`) and the reduced-motion hook (`usePrefersReducedMotion`) live in `shell/src/design/`. A drawing is a component from `shell/src/design/illustrations/` (`<Welcome decorative size="md" />`): `sm` in a grid or a loader, `md` on a page, `lg` in the gallery; it is hidden from assistive technology when the text beside it says the same, labelled otherwise. The tenant's mark is `<TenantMark hostname={window.location.hostname} size={24} />` from `shell/src/design/patterns/`. A new drawing follows `docs/design/illustration-checklist.md` and gets its tile in the gallery.

## A module on the home page and in the header (D4, ADR-0033)

The home page, the search and the help panel read the manifests, so a module appears in them without a change to the shell.

1. **The opener.** `opener: { accent: "identity", lineKey: "modules.identity.line" }` in the manifest. The accent is one of the accents in `shell/src/design/tokens.ts` (a new module gets its own there, with its meaning in the catalogue); the line says in one sentence what the module is for. The mark is drawn for the module: the mandala generator with the module's name as the seed and one set of parameters for every module (`<ModuleMark name="identity" accent="identity" size={56} />`, the same mark beside the title of its first page). A spot drawing of the module's own goes in `drawing`: a component in `shell/src/design/illustrations/` in the brief's hand, through `docs/design/illustration-checklist.md`. `entries` says how many of its navigation entries the opener lists, three by default. The principal sees the openers of the modules it may open and, in each, only the entries it may open.
2. **Quick actions.** `quickActions: [{ id: "invite", labelKey: "users.invite.open", permission: "identity.users.manage", path: "/users?invite=1", icon }]`. The home page and the search offer the action when `/me/access` answers the permission at the principal's home unit. The page at `path` opens its dialog from the query and then removes the query, as `UsersPage` and `UnitsPage` do, so the home page, the search and a link start it the same way. A quick action without a permission fails the strict validation, as a route without one does.
3. **Help keys.** A route's `shortcuts` list its keys in the help panel on that page: `gridShortcuts`, `treeShortcuts` and `tabShortcuts` from `shell/src/components/shortcuts.ts`, or keys of its own with their words in the catalogue.
4. **Recently opened** names a page by its route's title; a record page names itself with `useRecentName(record.name)` once the record has loaded.
5. **The session warning** needs nothing from a page: every answer through `request()` counts as activity and moves the idle end in every tab. A page that holds unsaved work should still save as it goes; the warning gives two minutes.

## Proving a change (T17, T18, ADR-0022)

| Level | Command | What it proves |
|---|---|---|
| Unit and integration tests | `make test`, `make shell-check` | the code, on real platform services in containers |
| Accessibility | `make shell-a11y` | the gallery against WCAG 2.1 AA with axe in a real browser; no stack needed |
| Smoke | `make smoke` | the live stack from the outside: provisioning, tokens, cookies, denials, the trace |
| Browser | `make e2e` | login, home, the search, the grid, inviting a person, a child-unit administrator's view, and a plan changed in the control plane reaching the shell and the API within a minute, in a real browser |
| Pipeline's stage | `make integration` | platform and control plane from images, then the smoke |

Every pull request runs the first two levels in CI; every merge to main builds the images and runs the pipeline's stage on the runner. A change to a host or a module reaches a laptop tenant with `make images` and `make update-unit TENANT=<slug>`.
