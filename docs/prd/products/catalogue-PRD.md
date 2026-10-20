# Foundation: Catalogue PRD

## 0. Document control

| Item | Value |
|---|---|
| Document title | Catalogue: supplier items, supplier catalogues and shopping, Product Requirements Document |
| Layer | Products (ADR-0029); the module is proposed in section 13 |
| Identifier prefix | `CAT-`, in blocks of twenty per area (ADR-0041); the table is `catalogue-requirements.csv` |
| Version | 0.1 SKELETON (the split of 2026-10-20) |
| Date | 2026-10-20 |
| Status | Skeleton for the design session's checkpoint: the requirements moved from the Functional Foundation PRD under their new identifiers. The sections marked "after the checkpoint" are written then. |
| Inputs | The Functional Foundation PRD for procure-to-pay, version 1.2 (archived as `../archive/functional-foundation-p2p-PRD-v1.md`), the map (`../requirements-map.md`), ADR-0029 and ADR-0041. |
| Dependency | Items, categories, units and suppliers are the Business PRD's (BF-041 to BF-044, BF-101); contract items are the Contracts product's; every mechanism is a kernel capability cited by its TF identifier. |
| Tags | `[Carried]` behaviour from the current product that stays. `[Improved]` carried but changed to close a defect or gap. `[New]` not in the current product, standard practice. `[Decision]` needs the owner's confirmation; the question is in its notes. `[Later]` recorded, not in version 1. A requirement moved from the archived PRD says what it was at the start of its notes. |

Change log: 0.1 SKELETON, 2026-10-20: the split (ADR-0041). Supplier items, catalogues and shopping moved here from the Functional Foundation PRD 1.2, their words unchanged except for names that changed. The item master went to the Business PRD.

## 1. Vision and goals

### 1.1 Vision

Approved content to buy from: supplier items with their prices, catalogues that suppliers maintain and buyers accept, published to the organisations that may use them, and a shopping experience that turns a cart into a request, with contract items at contract prices.

### 1.2 Goals (outcomes)

Written after the checkpoint.

### 1.3 Lessons from the current product

Keep, from the archived PRD's list: catalogue revisions accepted by the buyer.

## 2. Scope

### 2.1 In scope

Supplier items, catalogue upload, revisions and acceptance, publishing per organisation, price validity, supplier item imports, browse and cart, contract items in browsing, search and favourites, and the catalogue's reports.

### 2.2 Out of scope in version 1 (`[Later]`)

From the archived PRD's list: punch-out catalogues.

## 3. Personas

| Persona | Goals in this product | Surface |
|---|---|---|
| Requester | Raise a request from the catalogue | Enterprise app |
| Buyer | Maintain catalogues | Enterprise app |
| Supplier primary contact | Upload catalogues per customer | Supplier Network portal |

## 4. Domain model

```mermaid
erDiagram
  SUPPLIER ||--o{ CATALOGUE : publishes
  CATALOGUE ||--o{ CATALOGUE_REVISION : "revised by"
  CATALOGUE ||--o{ CATALOGUE_ITEM : lists
  ITEM ||--o{ SUPPLIER_ITEM : "offered as"
```

`SUPPLIER` and `ITEM` are the Business masters (BF-101, BF-041), drawn for context.

## 5. Processes

The requirements describe it: a supplier uploads a catalogue, which creates a revision with a diff; the buyer accepts or rejects it, whole or per line; accepted lines become current items; the buyer publishes the catalogue to organisations; requesters browse, fill a cart and convert it into a request. The process maps come with the catalogue's process reference pack (PM-001).

## 6. Requirements

Each requirement has an identifier, a priority (Must, Should, Could), a tag, the requirement statement in business terms, the reason, acceptance criteria that a tester can run (including at least one error case), dependencies and notes.

### 6.1 Supplier items and catalogues (CAT-001 to CAT-020)

#### CAT-001 Supplier items
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier items and catalogues

**Requirement:** Supplier items link an item to a supplier with supplier part number, price, currency, unit, lead time, minimum order quantity, availability and validity dates.

**Why:** Supplier-specific prices.

**Acceptance criteria:**

- AC1 Given a supplier item in validity, When a request line picks item and supplier, Then the price is pre-filled.
- AC2 Given a duplicate item and supplier pair, When saved, Then it is refused. (error case)
- AC3 Given validity ended, When picked, Then the price is not pre-filled and a notice is shown.

*Depends on:* BF-041, BF-101

**Notes:** Was FF-041.

#### CAT-002 Catalogue upload and revisions
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier items and catalogues

**Requirement:** Suppliers shall upload catalogues (template download; CSV, spreadsheet or archive with images) creating a revision with lines; validation and duplicate checks run before the revision is submitted; buyers see a diff against the current catalogue (new, changed price, removed).

**Why:** Price list maintenance by suppliers.

**Acceptance criteria:**

- AC1 Given a valid file, When uploaded, Then a revision in Submitted status with a diff exists and the buyer is notified.
- AC2 Given duplicate part numbers in the file, When uploaded, Then it is refused listing them. (error case)
- AC3 Given a changed price, When the diff is viewed, Then old and new prices and percent change are shown.

*Depends on:* BF-101, TF-205

**Notes:** Was FF-042.

#### CAT-003 Catalogue revision acceptance
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier items and catalogues

**Requirement:** Buyers shall accept or reject a revision (whole or per line, with comments); accepted lines become current catalogue items with validity; rejected lines are reported to the supplier.

**Why:** Buyer control of prices.

**Acceptance criteria:**

- AC1 Given a revision accepted, When the catalogue is viewed, Then lines reflect it and the supplier is notified.
- AC2 Given lines rejected with comments, When the supplier views the revision, Then the comments are shown per line.
- AC3 Given a revision accepted twice, When attempted, Then it is refused. (error case)

*Depends on:* CAT-002

**Notes:** Was FF-043.

#### CAT-004 Catalogue publishing
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier items and catalogues

**Requirement:** Buyers shall publish catalogues with accepted lines to requesters (optionally per organisation unit) and unpublish them; only published lines in validity are orderable.

**Why:** Only approved content is bought from.

**Acceptance criteria:**

- AC1 Given a published catalogue, When a requester browses, Then its lines appear.
- AC2 Given a catalogue without accepted lines, When published, Then it is refused. (error case)
- AC3 Given a line past validity, When browsed, Then it is hidden.

*Depends on:* CAT-003

**Notes:** Was FF-044.

#### CAT-005 Price validity and currency rules
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Supplier items and catalogues

**Requirement:** Catalogue and supplier item prices shall have validity dates and currencies; price changes shall never apply to issued orders; expiring prices shall be reported to buyers 30 days before.

**Why:** Stable orders, timely renewals.

**Acceptance criteria:**

- AC1 Given a price changed after an order is issued, When the order is viewed, Then its price is unchanged.
- AC2 Given a price expiring in 30 days, When the job runs, Then the buyer is notified.
- AC3 Given a price in a currency not allowed for the tenant, When uploaded, Then it is refused. (error case)

*Depends on:* CAT-001, CAT-003

**Notes:** Was FF-047.

#### CAT-006 Supplier item imports
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier items and catalogues

**Requirement:** Supplier items shall be importable through the kernel import framework with templates and validation.

**Why:** Bulk loading at go-live.

**Acceptance criteria:**

- AC1 Given a valid supplier item file, When imported, Then supplier items are created for their items and suppliers.
- AC2 Given rows with unknown units, When imported, Then they are reported and skipped. (error case)
- AC3 Given a dry run, When finished, Then nothing is created and the report shows counts.

*Depends on:* CAT-001, TF-205

**Notes:** Was FF-049, the supplier items; items are BF-044. AC1 is written for the split, after FF-049 AC1.

#### CAT-007 Catalogue applicability per organisation
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Supplier items and catalogues

**Requirement:** Catalogues shall be published per organisation or to all; browsing shows only items published to the user's organisation.

**Why:** Different units buy from different agreements.

**Acceptance criteria:**

- AC1 Given a catalogue published to A, When a user of B browses, Then its items are hidden.
- AC2 Given a catalogue item published only to organisation A, When a user of organisation B requests it by identifier, Then it is refused. (error case)

*Depends on:* CAT-004, BF-003

**Notes:** Was FF-186, the catalogues; the contracts are CON-006. AC2 is written for the split, after CAT-022 AC4.

### 6.2 Shopping (CAT-021 to CAT-040)

#### CAT-021 Browse and cart
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Shopping

**Requirement:** Requesters shall browse published catalogue and contract items across suppliers with search, filters (category, supplier, price) and favourites, add to a cart with quantities, and convert the cart into a request with one line per item and supplier pre-filled.

**Why:** Shopping experience.

**Acceptance criteria:**

- AC1 Given items in the cart, When converted, Then a draft request exists with the lines, prices and suppliers.
- AC2 Given an empty cart, When converted, Then a message says the cart is empty. (error case)
- AC3 Given a favourite, When the requester returns, Then it is listed first.

*Depends on:* CAT-004, REQ-001

**Notes:** Was FF-045.

#### CAT-022 Contract items in the catalogue
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Shopping

**Requirement:** Published contract items shall appear in browsing with contract price and remaining quantity or value, and lines created from them shall carry the contract reference.

**Why:** Contract compliance by default.

**Acceptance criteria:**

- AC1 Given a contract item picked, When the request line is created, Then it references the contract and uses its price.
- AC2 Given a contract expired, When browsed, Then its items are hidden.
- AC3 Given remaining quantity zero, When picked, Then a notice is shown and the line is created without contract reference. (edge)
- AC4 Given a contract item of an unpublished contract, When requested by identifier, Then it is refused. (error case)

*Depends on:* CON-002

**Notes:** Was FF-046.

#### CAT-023 Catalogue search and favourites
*Priority:* **Could** · *Tag:* `[New]` · *Area:* Shopping

**Requirement:** Search shall cover item names, descriptions, part numbers and attributes with synonyms configurable by the tenant; users may keep favourites and recent items.

**Why:** Findability.

**Acceptance criteria:**

- AC1 Given a synonym configured, When searched, Then matching items appear.
- AC2 Given no results, When searched, Then suggestions by category are shown.
- AC3 Given recent items, When the catalogue opens, Then they are listed. (edge)
- AC4 Given a synonym list upload with invalid rows, When imported, Then the rows are reported and skipped. (error case)

*Depends on:* CAT-021, TF-204

**Notes:** Was FF-048.

### 6.3 Reports (CAT-041 to CAT-060)

#### CAT-041 Operational reports
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Reports

**Requirement:** The reports in section 11.3 shall be available as grids with filters, saved views and export, respecting permissions and visibility.

**Why:** Run the operation.

**Acceptance criteria:**

- AC1 Given a user without the reports permission, When opening, Then it is refused. (error case)
- AC2 Given export, When run, Then the file matches the grid.

*Depends on:* TF-171, TF-203

**Notes:** Was FF-160, the catalogue reports; the others are REQ-061, ORD-081, RCV-101 and INV-084. Placed by the test: each product owns its reports.

## 7. Supplier views

What the supplier sees and does in the portal for this product; the narrative is written after the checkpoint. The requirements (CAT-061 to CAT-080):

#### CAT-061 Catalogue in the portal
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier views

**Requirement:** Suppliers download templates, upload catalogues, see revisions with diffs and buyer comments, and current published items.

**Why:** Supplier-maintained content.

**Acceptance criteria:**

- AC1 Given an upload, When valid, Then a revision with diff appears.
- AC2 Given a rejected line, When viewed, Then the buyer comment is shown.
- AC3 Given no accepted revision, When items are viewed, Then an empty state explains. (edge)
- AC4 Given a file over the size limit, When uploaded, Then it is refused with the limit shown. (error case)

*Depends on:* CAT-002, CAT-003

**Notes:** Was FF-156.

## 8. Rules and state machines

### 8.1 Key rules

None of the archived PRD's key rules is this product's alone.

### 8.2 State machines

From the archived PRD's section 7.2: Catalogue: Draft → Accepted → Published ↔ Unpublished; Revision: Submitted → Accepted or Rejected.

## 9. Records and signatures (Part 11)

Which records are approved, acknowledged or signed and so need electronic signatures (TF-063, TF-219), which need an audit-trail review, and their retention class (TF-066). Written after the checkpoint.

## 10. Visibility tags

How this product's records take their tags (TF-241, rule 6). Written after the checkpoint.

## 11. Data, integration and reports

### 11.1 Data notes

Written after the checkpoint.

### 11.2 Integrations

None in the archived PRD's table is this product's.

### 11.3 Reports

From the archived PRD's section 8.4: catalogue status.

### 11.4 Search, exports and analytics feed

What this product indexes for search (TF-204), exports for audit and the ERP (TF-203, TF-205, TF-206) and feeds to analytics (TF-089, TF-203), as the archived FF-164 to FF-166 asked. Declared after the checkpoint.

## 12. Dependencies

The business and kernel requirements this product needs, by identifier. Written after the checkpoint.

## 13. Layer, module, schema and permissions

Written after the checkpoint; the proposed module is `Iron.Products.Catalogue` (schema `products_catalogue`). The archived PRD's permission catalogue (its section 8.3) named `catalogue.catalogue.{read,accept,reject,publish}` for catalogues; its `catalogue.item.*` is now the Business item master's. Permission names name the module, so they follow it once it is settled.

## 14. Configuration

Written after the checkpoint.

## 15. Slices

Where the moved requirements sat in the archived PRD's build plan (its section 9). The product slices are proposed in the technical PRD's section 7 after the checkpoint.

| Slice of the archived plan | Its outcome | This document's requirements from it |
|---|---|---|
| P2 | Suppliers and orders | CAT-007 |
| P4 | Invoicing and matching | CAT-041 |
| P5 | Catalogue, contracts, change orders | CAT-001, CAT-002, CAT-003, CAT-004, CAT-005, CAT-006, CAT-021, CAT-022, CAT-023 |
| P6 | Completeness | CAT-061 |

## 16. Decisions and open points

### 16.1 Decisions

None of the archived PRD's decisions is this product's alone.

### 16.2 Open points

From the archived PRD's section 10.2:

| ID | Question | Default used |
|---|---|---|
| G-08 | Catalogues: one catalogue per customer (default) or one network catalogue offered to many customers? | Per customer in version 1; network catalogue later |

From the split: CAT-022 (contract items in browsing) depends on the Contracts product's CON-002; the module dependency between the two products is settled with the modules after the checkpoint.

## 17. Glossary

Written after the checkpoint.
