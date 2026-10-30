# ADR-0041: One requirements document per layer and product

**Status:** Accepted
**Date:** 2026-10-20
**Decided by:** Product owner and architect

## Context

ADR-0029 placed every piece of Foundation in one of three layers: kernel, business, products. The requirements still lived in two documents: the Technical Foundation PRD, which is the kernel in substance, and the Functional Foundation PRD for procure-to-pay, which mixed the shared business masters with the products that use them. A business slice cannot start from a document that does not say which layer a requirement belongs to, and the S3 task that tags tests with the requirements they prove (TF-218) needs identifiers that stay put. The design session decided the split on 2026-10-20 (`docs/prompts/PRD-split.md`).

## Decision

1. **One document per layer and product**, in `docs/prd/`:

   | Document | File | Identifier prefix |
   |---|---|---|
   | Technical Foundation PRD (the kernel) | `foundation/technical-foundation-PRD.md` (kept; in `foundation/` since ADR-0043) | `TF-` (kept) |
   | Business Foundation PRD | `foundation/business-foundation-PRD.md` | `BF-` |
   | Supplier management | `suites/procurement/supplier-management-PRD.md` | `SUP-` |
   | Catalogue | `suites/procurement/catalogue-PRD.md` | `CAT-` |
   | Purchase requests | `suites/procurement/requests-PRD.md` | `REQ-` |
   | Purchase orders | `suites/procurement/orders-PRD.md` | `ORD-` |
   | Receiving (shipment notices, goods and service receipts) | `suites/procurement/receiving-PRD.md` | `RCV-` |
   | Invoicing | `suites/procurement/invoicing-PRD.md` | `INV-` |
   | Contracts | `suites/procurement/contracts-PRD.md` | `CON-` |
   | Supplier portal and Supplier Network experience (a surface) | `suites/procurement/supplier-portal-PRD.md` | `SPX-` |
   | Product method (binding for every business and product document) | `product-method.md` | `PM-` |
   | Sourcing (RFx), Auctions, Projects and tasks, Surveys, Expenses | `suites/procurement/<name>-scope.md`, one page each | `SRC-`, `AUC-`, `PRJ-`, `SRV-`, `EXP-` reserved |

   Changed 2026-10-30 by ADR-0058: the scope pages are those of the eProcurement Suite's module list (`suites/procurement/README.md`). Auctions merged into Sourcing, and `AUC-` stays reserved and unused; Projects and tasks became Category and Savings Projects, keeping `PRJ-`; Surveys left the suite for the forms engine, and `SRV-` is retired; `ANL-`, `RSK-`, `PAY-` and `SVC-` are reserved for the four new modules.

2. **A CSV next to every requirements document**, named after it (`requests-PRD.md` has `requests-requirements.csv`, `product-method.md` has `product-method-requirements.csv`), with the columns of the first two: `id`, `area`, `title`, `priority`, `tag`, `requirement`, `rationale`, `acceptance_criteria`, `depends_on`, `notes`, plus `was`, the old identifier when there is one. Lists inside a cell are joined with `|`. The markdown is the source; `make prd-csv` writes the CSVs from it.
3. **Identifiers are stable and never reused.** They are numbered in blocks of twenty per area: the first area of a document holds 001 to 020, the second 021 to 040, and so on, so an area can grow without renumbering the next. An identifier, once given, is never given to another requirement, even after its requirement is retired.
4. **New identifiers everywhere outside the kernel.** Every requirement of the Functional Foundation PRD gets a new identifier in its new home and keeps its old one in the `was` column and at the start of its notes ("Was FF-022."). The functional PRD moves to `docs/prd/archive/functional-foundation-p2p-PRD-v1.md`, its CSV beside it, with a first line saying it is superseded and pointing to the map. TF identifiers do not change.
5. **The map.** `docs/prd/requirements-map.md` and `requirements-map.csv` list every FF identifier with its new identifier or identifiers, their documents, and what happened to it: moved, split, merged, covered by a kernel requirement, or retired with a reason. A row covered by the kernel names the TF requirement and the task that built it. A row the brief did not settle is placed by the placement test of ADR-0029 and says so, with a sentence of reason.
6. **One form of acceptance criteria** in every document: `AC1 Given ..., When ..., Then ...`, with at least one error case per requirement, marked `(error case)`. A test can then name what it proves, for example `REQ-012 AC2` (TF-218).
7. **Moved text is kept.** A moved requirement keeps its words; only names that changed are changed: the layers and kernel modules ("Foundation workflow" becomes "kernel workflow"), the identifiers it cites (an FF identifier becomes the new one) and the section numbers of its new document. A requirement split between documents keeps, in each part, the clauses and criteria that belong there; a criterion written for a part says so in the notes. Every new requirement is tagged `[New]`, and every requirement that needs the owner's confirmation `[Decision]`, with the question in its notes and a proposed default.
8. **The check.** `make prd-check` runs `tools/prd_check.py` (Python 3, standard library only) and CI runs it in the job that validates the compose file. Its own tests (`tools/prd_check_test.py`) run first and break each rule once. It refuses an identifier used twice across the documents and their CSVs, or one outside its document's prefix or its area's block of twenty; an FF identifier without a row in the map, or a map row whose new identifier is missing from its document or does not name the FF identifier in its `was`; a requirement without a criterion in the Given, When, Then form or without one marked `(error case)`; a product PRD without the sections "Supplier views" and "Records and signatures"; a CSV that differs from its markdown in any cell; and a dependency on an identifier that no document holds.

## Options considered

- Keep the two documents and add a layer column. Rejected: one document would still mix the masters every product reads with one product's documents, and a product team could not read its scope in one place.
- Keep the FF identifiers in their new homes. Rejected: `FF-110` says nothing about where it lives, and a prefix per document makes a test's tag (`INV-001 AC2`) point at one file.
- Number each document straight through. Rejected: a new requirement in an early area would take the next number at the end, so areas would interleave and the order would stop meaning anything.
- One CSV for all documents. Rejected: a product's requirements would not travel with its document, and every change would touch one shared file.
- The check in the shell's Node. Rejected: it reads markdown and CSV, nothing of the shell's; Python's standard library does that in one file with nothing to install, and CI's runner has Python 3.

## Consequences

- Easier: a requirement's prefix names its document and layer; a test names `REQ-012 AC2` and the validation pack (TF-218) can find it; a product team reads one document plus the Business PRD and the kernel requirements it cites; the map answers where any old identifier went.
- Harder: every requirements document is two files, kept equal by the check, so an edit of the markdown is followed by `make prd-csv`. ADRs and prompts before this one cite FF identifiers, and the map translates them.
- Revisit: when one of the five scope pages becomes a full PRD, it takes its reserved prefix and a CSV, and the check reads it like the others.
