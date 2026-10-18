# Business layer

The shared business masters and the business configuration of kernel mechanisms (ADR-0029): organisations and legal entities, purchasing groups, departments, people (designations, job grades, managers, business groups), items, commodities, categories, units of measure, chart of accounts, budgets, tax codes, HSN and GL codes, currencies and conversion rates, financial years and periods, payment and shipping terms, locations, projects as cost objects, the supplier master, content grouping, lookups, approval configurations and the delegation of authority matrices, number series, notification templates.

No projects yet. The first business module arrives after the Functional Foundation PRD is split into a Business PRD and one PRD per product (ADR-0029, consequences).

## The placement test

Before creating a module, ask where it belongs:

- It has no business meaning: **Kernel** (`src/Kernel/`).
- Two or more products would need to own it: **Business** (here).
- One product owns it: **Products** (`src/Products/`).

## The rule

- Business may use Kernel, never Products. Between modules, in any layer, only through a Contracts project.
- When the kernel needs something only Business can supply, the kernel declares an interface in its Contracts and a business module implements it; the kernel never references Business. The first one is the organisation kinds: `IOrganisationKinds` in `Iron.Kernel.Authorization.Contracts`, which `Iron.Business.Organisations` will implement with legal entity, business unit, purchasing group, department and team (A1).
- Every entity is a `BusinessRecord` (owning organisation and sharing mode) or marked `ITenantWide` (TF-220); the architecture tests check it.
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

1. Apply the placement test and name the products that need it in the module README.
2. Copy the layout of a kernel module (`src/Kernel/Iron.Kernel.Identity` is the template): `Api/`, `Services/`, `Data/`, `Contracts/` as its own project, and a `README.md`. Declare `<IronLayer>Business</IronLayer>` in both project files.
3. Name the context's schema `[Schema("business_<module>")]` and generate its first migration with `make ef-add MODULE=<Module> NAME=Initial`.
4. Add both projects to `Foundation.slnx` and to `tests/Iron.Architecture.Tests`, and the module to the hosts that serve it.
5. Run `make check`: it names any reference that points up or past a Contracts project.
