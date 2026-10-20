# Business layer

The shared business masters and the business configuration of kernel mechanisms (ADR-0029). With the kernel, this layer is IronSaaS Foundation, which every tenant has (ADR-0043): organisations and legal entities, purchasing groups, departments, people (designations, job grades, managers, business groups), items, commodities, categories, units of measure, chart of accounts, budgets, tax codes, HSN and GL codes, currencies and conversion rates, financial years and periods, payment and shipping terms, locations, projects as cost objects, the supplier master, content grouping, lookups, approval configurations and the delegation of authority matrices, number series, notification templates.

No projects yet. The Business Foundation PRD (`docs/prd/foundation/business-foundation-PRD.md`) holds the requirements, proposes the modules with their schemas and permissions (its section 9), and the technical PRD's section 7 proposes their slices, B1 to B4.

## The placement test

Before creating a module, ask where it belongs:

- It has no business meaning: **Kernel** (`src/Kernel/`).
- A master that enterprise products of any kind need, or the products of two or more suites: **Business** (here).
- Two or more products of one suite need it: that suite's **shared master** (`src/Suites/<Suite>/`).
- One product owns it: that **product** (`src/Suites/<Suite>/`).

## The rule

- Business may use Kernel, never a suite (ADR-0043). Between modules, in any layer, only through a Contracts project.
- When the kernel needs something only Business can supply, the kernel declares an interface in its Contracts and a business module implements it; the kernel never references Business. The first one is the organisation kinds: `IOrganisationKinds` in `Iron.Kernel.Authorization.Contracts`, which `Iron.Business.Organisations` will implement with legal entity, business unit, department and team (A1). A purchasing group is not a kind: it serves several units and owns no records, so it is a business master of its own (`docs/prd/foundation/business-foundation-PRD.md`, section 5.1), which corrects A1's list.
- Business never depends on a suite or a product (ADR-0042, ADR-0043). Where it must hear from one, it declares an extension point in its Contracts that the product implements; the first is the use of master records by the products (BF-207).
- Every entity is a `BusinessRecord` (owning organisation and sharing mode) or marked `ITenantWide` (TF-220); the architecture tests check it.
- A business module is licensed per module, in its own code (ADR-0034): its `IModule` declares its licence code and subscription type, and its features; the architecture test refuses one without a licence. A plan then includes it, and until a tenant's plan does, its endpoints answer `entitlements.module_disabled` and the shell hides it (the developer guide's "Licensing a module or feature").
- Permission names, audit events, error codes, routes and configuration sections name the module, not the layer: `<module>.<resource>.<action>`, never `business.<module>...`.

## Names

| What | Name |
|---|---|
| Project and root namespace | `Iron.Business.<Module>` |
| Folder | `src/Business/Iron.Business.<Module>/` |
| Contracts | `Iron.Business.<Module>.Contracts` in the module's `Contracts/` folder |
| Layer in both project files | `<IronLayer>Business</IronLayer>` |
| Schema | `business_<module>` |
| Tests | `tests/Business/Iron.Business.<Module>.Tests/` |
| Shell module | `shell/src/modules/business/<name>/` |

## Adding a business module

1. Apply the placement test and name, in the module README, the products that need it and the suites they belong to; a master only one suite's products need belongs in that suite.
2. Copy the layout of a kernel module (`src/Kernel/Iron.Kernel.Identity` is the template): `Api/`, `Services/`, `Data/`, `Contracts/` as its own project, and a `README.md`. Declare `<IronLayer>Business</IronLayer>` in both project files.
3. Name the context's schema `[Schema("business_<module>")]` and generate its first migration with `make ef-add MODULE=<Module> NAME=Initial`.
4. Add both projects to `Foundation.slnx` and to `tests/Iron.Architecture.Tests`, and the module to the hosts that serve it.
5. Run `make check`: it names any reference that points up or past a Contracts project.
6. Declare its licence and features, add it to `Iron.Host.Api.UnitModules`, run `make catalogue` and commit the catalogue; add `licence` to its shell manifest.
