# Product requirements

One requirements document per layer and product (ADR-0041), each with its CSV beside it, in the folders of ADR-0043: IronSaaS Foundation's two documents in `foundation/`, each suite's documents in `suites/<suite>/`.

## IronSaaS Foundation: `foundation/`

| Document | What it holds | Prefix |
|---|---|---|
| `foundation/technical-foundation-PRD.md` | The kernel: the SaaS platform's mechanisms | `TF-` |
| `foundation/business-foundation-PRD.md` | The business layer: the shared masters and the business configuration of kernel mechanisms | `BF-` |

## The eProcurement Suite: `suites/procurement/`

| Document | What it holds | Prefix |
|---|---|---|
| `suites/procurement/purchasing-PRD.md` | The suite's shared master: purchasing groups, the purchasing profiles of suppliers and people, the starter approvals and readiness items | `PUR-` |
| `suites/procurement/supplier-management-PRD.md` | The supplier's life cycle | `SUP-` |
| `suites/procurement/catalogue-PRD.md` | Supplier items, catalogues and shopping | `CAT-` |
| `suites/procurement/requests-PRD.md` | Purchase requests | `REQ-` |
| `suites/procurement/orders-PRD.md` | Purchase orders | `ORD-` |
| `suites/procurement/receiving-PRD.md` | Shipment notices, goods and service receipts, returns | `RCV-` |
| `suites/procurement/invoicing-PRD.md` | Invoices, matching, payment status, credit notes | `INV-` |
| `suites/procurement/contracts-PRD.md` | Contracts | `CON-` |
| `suites/procurement/supplier-portal-PRD.md` | The supplier portal and the Supplier Network experience, a surface | `SPX-` |
| `suites/procurement/<name>-scope.md` | Sourcing, auctions, projects and tasks, surveys, expenses: one page each, no requirements yet | `SRC-`, `AUC-`, `PRJ-`, `SRV-`, `EXP-` reserved |

## Beside them, at the top of this folder

| Document | What it holds | Prefix |
|---|---|---|
| `product-method.md` | The product method, binding for the business layer and every suite's products | `PM-` |
| `requirements-map.md` | Where every requirement of the Functional Foundation PRD for procure-to-pay went; that PRD is archived in `archive/` | |

## How to cite and change them

A requirement is cited by its identifier, and a test names what it proves by the identifier and the criterion: `REQ-012 AC2` (TF-218). Identifiers are stable and never reused, and did not change when the documents moved to their folders; each area of a document has a block of twenty, and a moved requirement starts its notes with every identifier it had ("Was FF-022.", "Was FF-174 and BF-201."), and the map lists each move, those after the split included.

A requirement that others replace is retired, not deleted: it keeps its identifier, its words and its place, takes the tag `[Retired]`, and its notes say when and what replaced it ("Retired 2026-10-21 (SUP-gaps). Replaced by SUP-201 to SUP-204 and SUP-206 to SUP-211."). Its identifier is never given again (ADR-0041, decision 3). Nothing depends on a retired requirement and no test names it; they name its successors, and `make prd-check` and `make trace` refuse anything else.

A module's process reference pack (PM-001) lives in `suites/<suite>/process-reference/<module>.md`, named after its document (`supplier-management.md` is the pack of `supplier-management-PRD.md`), with its BPMN maps in the folder of the same name beside it. A pack is not a requirements document: its section 2 maps every live requirement of its module to a PCF element and a map step, which `make prd-check` holds, and each map is a BPMN 2.0 file valid against OMG's schema (ADR-0045).

Requirements depend down the layers: the kernel, the business layer, a suite's shared masters, its products, its surfaces. The Foundation holds mechanisms and generic masters, and a suite brings its own content (ADR-0043, decision 8). Between the eProcurement Suite's products they follow the process order: Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing (ADR-0042). A dependency on a later product is an event and is written so: `RCV-081 (event)`. Business never depends on a product; where it must hear from one, it declares an extension point (the Business PRD's section 6.2). A suite's document never depends on another suite's (ADR-0043).

A new suite gets its own folder, `suites/<suite>/`, with its documents' prefixes added to `tools/prd_check.py` and its process order beside the eProcurement Suite's.

The markdown is the source: after editing a document, `make prd-csv` writes its CSV, and `make prd-check` checks every document, its CSV and the map (CI runs it). The build plan in slices is section 7 of the technical PRD. Each slice has a plan under `docs/slices/`.
