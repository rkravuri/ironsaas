# Product requirements

One requirements document per layer and product (ADR-0041), each with its CSV beside it:

| Document | What it holds | Prefix |
|---|---|---|
| `technical-foundation-PRD.md` | The kernel: the SaaS platform's mechanisms | `TF-` |
| `business-foundation-PRD.md` | The business layer: the shared masters and the business configuration of kernel mechanisms | `BF-` |
| `products/supplier-management-PRD.md` | The supplier's life cycle | `SUP-` |
| `products/catalogue-PRD.md` | Supplier items, catalogues and shopping | `CAT-` |
| `products/requests-PRD.md` | Purchase requests | `REQ-` |
| `products/orders-PRD.md` | Purchase orders | `ORD-` |
| `products/receiving-PRD.md` | Shipment notices, goods and service receipts, returns | `RCV-` |
| `products/invoicing-PRD.md` | Invoices, matching, payment status, credit notes | `INV-` |
| `products/contracts-PRD.md` | Contracts | `CON-` |
| `surfaces/supplier-portal-PRD.md` | The supplier portal and the Supplier Network experience, a surface | `SPX-` |
| `product-method.md` | The product method, binding for the business layer and every product | `PM-` |
| `products/<name>-scope.md` | Sourcing, auctions, projects and tasks, surveys, expenses: one page each, no requirements yet | `SRC-`, `AUC-`, `PRJ-`, `SRV-`, `EXP-` reserved |

`requirements-map.md` (and its CSV) says where every requirement of the Functional Foundation PRD for procure-to-pay went; that PRD is archived in `archive/`.

A requirement is cited by its identifier, and a test names what it proves by the identifier and the criterion: `REQ-012 AC2` (TF-218). Identifiers are stable and never reused; each area of a document has a block of twenty, and a moved requirement starts its notes with what it was ("Was FF-022.").

Requirements depend down the layers, the kernel, the business layer, the products and the supplier portal surface, and between products along the process order: Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing (ADR-0042). A dependency on a later product is an event and is written so: `RCV-081 (event)`. Business never depends on a product; where it must hear from one, it declares an extension point (the Business PRD's section 6.2).

The markdown is the source: after editing a document, `make prd-csv` writes its CSV, and `make prd-check` checks every document, its CSV and the map (CI runs it). The build plan in slices is section 7 of the technical PRD. Each slice has a plan under `docs/slices/`.
