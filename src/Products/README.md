# Products layer

Documents and processes with their own life cycles (ADR-0029): supplier onboarding, purchase requests, purchase orders, advanced shipment notices, goods and service receipts, invoicing, sourcing (RFx), auctions, contracts, projects and tasks as a product, forms and surveys, expenses. The supplier portal is a surface, not a product: each product contributes its supplier views.

No projects yet. Product work starts after slice S2 (PRD section 7), from one PRD per product.

## The placement test

Before creating a module, ask where it belongs:

- It has no business meaning: **Kernel** (`src/Kernel/`).
- Two or more products would need to own it: **Business** (`src/Business/`).
- One product owns it: **Products** (here).

A master that a second product turns out to need moves down to Business; a product never owns what another product needs.

## The rule

- Products may use Business and Kernel, always through their Contracts projects. A product never references another product's implementation.
- Every entity is a `BusinessRecord` (owning organisation and sharing mode) or marked `ITenantWide` (TF-220); the architecture tests check it.
- Permission names, audit events, error codes, routes and configuration sections name the product, not the layer: `<product>.<resource>.<action>`, never `products.<product>...`.

## Names

| What | Name |
|---|---|
| Project and root namespace | `Iron.Products.<Product>` |
| Folder | `src/Products/Iron.Products.<Product>/` |
| Contracts | `Iron.Products.<Product>.Contracts` in the product's `Contracts/` folder |
| Layer in both project files | `<IronLayer>Products</IronLayer>` |
| Schema | `products_<product>` |
| Tests | `tests/Products/Iron.Products.<Product>.Tests/` |
| Shell module | `shell/src/modules/products/<name>/` |

## Adding a product

1. Apply the placement test to every master the product needs: those two or more products need belong in Business first.
2. Copy the layout of a kernel module (`src/Kernel/Iron.Kernel.Identity` is the template): `Api/`, `Services/`, `Data/`, `Contracts/` as its own project, and a `README.md`. Declare `<IronLayer>Products</IronLayer>` in both project files.
3. Name the context's schema `[Schema("products_<product>")]` and generate its first migration with `make ef-add MODULE=<Product> NAME=Initial`.
4. Add both projects to `Foundation.slnx` and to `tests/Iron.Architecture.Tests`, and the product to the hosts that serve it.
5. Run `make check`.
