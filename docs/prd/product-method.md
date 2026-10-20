# Foundation: Product method

## 0. Document control

| Item | Value |
|---|---|
| Document title | The product method: process reference and research basis, binding for the Business PRD and every product PRD |
| Identifier prefix | `PM-`, in blocks of twenty per area (ADR-0041); the table is `product-method-requirements.csv` |
| Version | 0.2 DRAFT for review (the split of 2026-10-20) |
| Date | 2026-10-20 |
| Status | Draft for review: the requirements moved from the Functional Foundation PRD under their new identifiers. |
| Inputs | The Functional Foundation PRD for procure-to-pay, version 1.2 (archived as `archive/functional-foundation-p2p-PRD-v1.md`), its section 2.4 and area 6.14, the map (`requirements-map.md`), the Technical Foundation PRD's P17 and TF-240, ADR-0041. |
| Binding | Every business module and every product follows this method before its first slice. A product's PRD cites the requirements here; it does not restate them. |
| Tags | `[Carried]` behaviour from the current product that stays. `[New]` not in the current product, standard practice. `[Decision]` needs the owner's confirmation; the question is in its notes. A requirement moved from the archived PRD says what it was at the start of its notes. |

Change log: 0.2 DRAFT, 2026-10-20: reviewed at the split's checkpoint without change. 0.1 SKELETON, 2026-10-20: the split (ADR-0041). The process reference and research basis of the Functional Foundation PRD 1.2 moved here, binding for every business and product document; the words are unchanged except for names that changed. The reference data from standards (was FF-202) went to the Business PRD as BF-205.

## 1. Purpose

From the archived PRD's section 2.4: the business layer and every product are grounded in industry research and process documentation before design (Technical Foundation P17, TF-240). What the team already knows is the starting point, never the limit.

## 2. References

### 2.1 The research basis for procure-to-pay

| Reference | Use in this product |
|---|---|
| APQC Process Classification Framework, category 4 "Procure materials and services" (4.1 to 4.4) and category 9 "Manage financial resources" for payables | Process map backbone; every requirement maps to a PCF activity |
| CIPS (Chartered Institute of Procurement and Supply) and ISM (Institute for Supply Management) practice guides; The Hackett Group procurement benchmarks | Control points, segregation of duties, performance indicators and target values (first-pass match, cycle times, cost per order) |
| Peppol BIS Billing 3.0, UBL 2.1 (order, order response, despatch advice, receipt advice, invoice, credit note), UN/CEFACT cross-industry invoice | Document field models and e-invoicing readiness; mandatory fields present or excluded with reason |
| UNSPSC for categories and commodities; GS1 (GTIN, GLN) for item and location identifiers; ISO 4217 currencies; ISO 8601 dates; UN/LOCODE locations; ISO 20022 for payment status messages; Incoterms 2020 for shipping terms | Reference data and identifier formats |
| GSTN rules for Indian GST and e-invoicing (accelerator pack); EU VAT directive principles for later packs | Country accelerator packs |
| Leading products studied for capability parity and gaps (enterprise source-to-pay suites and supplier networks) | Competitive feature review in the process reference pack |
| SOX-style financial controls, COSO | Approval limits, segregation of duties, evidence |

Each module carries a process reference pack (process maps with variants and exceptions, standards mapping, benchmark indicators, competitive review, research log) reviewed by an external practitioner before its first slice. The requirements in section 3 define this; the reference data from standards is the Business PRD's BF-205.

### 2.2 The later products

Sourcing, auctions, projects and tasks, surveys and expenses each start their full PRD from their own process reference pack (PM-001), with the references of their process added to this section when it is written.

## 3. Requirements

Each requirement has an identifier, a priority (Must, Should, Could), a tag, the requirement statement in business terms, the reason, acceptance criteria that a tester can run (including at least one error case), dependencies and notes.

### 3.1 Process reference (PM-001 to PM-020)

#### PM-001 Process reference pack per module
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Process reference

**Requirement:** Before the first slice of each module (the business masters, supplier management, catalogue, requests, orders, receiving, invoicing, contracts, the supplier portal surface, and every later product), a process reference pack shall exist and be approved: APQC PCF activity mapping, process maps (BPMN) for the main flow and each variant and exception, standards mapping (section 2.1), benchmark indicators with targets, competitive feature review of leading suites and networks, open gaps, and a research log with sources and dates.

**Why:** Decision F-17: not limited by what we know today.

**Acceptance criteria:**

- AC1 Given a module slice is scheduled, When the design review runs, Then the pack is present and approved, or the slice is blocked.
- AC2 Given a pack, When reviewed, Then every requirement of the module maps to a PCF activity and a process map step, or an exclusion is recorded.
- AC3 Given a pack without the competitive review, When submitted for approval, Then it is refused. (error case)

*Depends on:* TF-240

**Notes:** Was FF-200. The list of modules follows the split: the method binds the business masters and every product (ADR-0041).

#### PM-002 External practitioner review
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Process reference

**Requirement:** Each process reference pack and each module design shall be reviewed by at least one external practitioner with recent experience of a leading suite or network; review findings shall be recorded with disposition (accepted, deferred, rejected with reason) before the first slice of the module starts.

**Why:** Independent challenge of the team's assumptions.

**Acceptance criteria:**

- AC1 Given a review completed, When the module slice starts, Then the findings and dispositions are recorded.
- AC2 Given a module without a review record, When the slice is scheduled, Then it is blocked. (error case)
- AC3 Given a deferred finding, When the next version is planned, Then it appears in the backlog with its source.

*Depends on:* PM-001

**Notes:** Was FF-204.

#### PM-003 Research log and living process documentation
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Process reference

**Requirement:** The research log (sources, dates, findings, decisions) and the process maps shall be kept with the products' documentation, updated with each release that changes a process, and published to customers as process documentation per module.

**Why:** Documentation that stays true.

**Acceptance criteria:**

- AC1 Given a release that changes a process, When published, Then the process map version matches the release.
- AC2 Given a customer administrator, When they open process documentation, Then the current maps and variants are shown.
- AC3 Given a process change merged without a documentation update, When the release checklist runs, Then it is blocked. (error case)

*Depends on:* PM-001, TF-209

**Notes:** Was FF-205.

### 3.2 Standards and benchmarks (PM-021 to PM-040)

#### PM-021 Standards alignment of document models
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Standards and benchmarks

**Requirement:** The data models of orders, order responses, shipment notices, receipts, invoices and credit notes shall be mapped field by field to UBL 2.1 and Peppol BIS Billing 3.0 (and UN/CEFACT where relevant); mandatory fields of the standards shall exist in the model or be recorded as exclusions with reasons; exports in UBL shall be possible for invoices and orders in a later slice.

**Why:** Interoperability and e-invoicing readiness.

**Acceptance criteria:**

- AC1 Given the invoice model, When compared with Peppol BIS Billing 3.0, Then every mandatory element has a field or a recorded exclusion.
- AC2 Given an order, When exported as UBL (later slice), Then it validates against the schema.
- AC3 Given a new field added to the invoice model, When the standards mapping is not updated, Then the review is blocked. (error case)

*Depends on:* PM-001, INV-001

**Notes:** Was FF-201.

#### PM-022 Benchmark indicators built in
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Standards and benchmarks

**Requirement:** The products shall compute the benchmark indicators from the research basis (first-pass match rate, invoice cycle time, order cycle time, acknowledgement time, catalogue and contract compliance, cost per order inputs, supplier on-time delivery, electronic invoice share) with the same definitions as the reference sources so that customers can compare with industry benchmarks.

**Why:** Customers judge the products by these numbers.

**Acceptance criteria:**

- AC1 Given a quarter of data, When indicators compute, Then each shows value, definition and the reference it follows.
- AC2 Given a definition changed in a later version, When computed, Then both the old and new values are available for the transition period. (edge)
- AC3 Given insufficient data, When computed, Then the indicator shows "insufficient data" with the minimum needed. (error case)

*Depends on:* ORD-082, INV-085, PM-001

**Notes:** Was FF-203. "The product" became the products.

## 4. Slices

Where the moved requirements sat in the archived PRD's build plan (its section 9): its slice P0 came first, the process reference packs for all modules.

| Slice of the archived plan | Its outcome | This document's requirements from it |
|---|---|---|
| P0 | Process reference packs | PM-001, PM-002, PM-003, PM-021, PM-022 |

## 5. Decisions and open points

### 5.1 Decisions

Confirmed on 2026-10-17, from the archived PRD's section 10.1:

| ID | Decision | Where applied |
|---|---|---|
| F-17 | Functional foundation and modules backed by industry research and strong process documentation | Section 2.1, PM-001 to PM-003, PM-021 and PM-022, slice P0 |

### 5.2 Open points

From the archived PRD's section 10.2:

| ID | Question | Default used |
|---|---|---|
| G-09 | External practitioners for process reference reviews: who, and how many per module? | At least one practitioner per module with recent experience in a leading suite |
