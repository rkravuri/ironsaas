# Foundation: Supplier management PRD

## 0. Document control

| Item | Value |
|---|---|
| Document title | Supplier management: the supplier's life cycle with the tenant, Product Requirements Document |
| Layer | Products (ADR-0029); the module is proposed in section 13 |
| Identifier prefix | `SUP-`, in blocks of twenty per area (ADR-0041); the table is `supplier-management-requirements.csv` |
| Version | 0.1 SKELETON (the split of 2026-10-20) |
| Date | 2026-10-20 |
| Status | Skeleton for the design session's checkpoint: the requirements moved from the Functional Foundation PRD under their new identifiers. The sections marked "after the checkpoint" are written then. |
| Inputs | The Functional Foundation PRD for procure-to-pay, version 1.2 (archived as `../archive/functional-foundation-p2p-PRD-v1.md`), the map (`../requirements-map.md`), ADR-0029 and ADR-0041. |
| Dependency | The supplier record and its parts are the Business PRD's (BF-101 to BF-108); every mechanism is a kernel capability cited by its TF identifier. The supplier's own screens are served through the supplier portal surface (`../surfaces/supplier-portal-PRD.md`). |
| Tags | `[Carried]` behaviour from the current product that stays. `[Improved]` carried but changed to close a defect or gap. `[New]` not in the current product, standard practice. `[Decision]` needs the owner's confirmation; the question is in its notes. `[Later]` recorded, not in version 1. A requirement moved from the archived PRD says what it was at the start of its notes. |

Change log: 0.1 SKELETON, 2026-10-20: the split (ADR-0041). The supplier's life cycle moved here from the Functional Foundation PRD 1.2, its words unchanged except for names that changed.

## 1. Vision and goals

### 1.1 Vision

Only vetted suppliers receive orders. A buyer invites a supplier, the supplier registers once in the Supplier Network and completes the tenant's sections, the tenant checks for duplicates, approves and publishes it, and then controls its status, its changes, its bank details, its documents, its questionnaires, its risks and its performance for as long as the relationship lasts.

### 1.2 Goals (outcomes)

Written after the checkpoint. The archived PRD's goal 3 (suppliers self-serve, onboarding included) is the supplier portal surface's goal 1.

### 1.3 Lessons from the current product

Improve, from the archived PRD's list: supplier ownership on every portal operation.

## 2. Scope

### 2.1 In scope

Onboarding (invitation, registration, duplicates, approval and publishing, onboarding configuration), changes and status (status control, change approval, bank account verification, merge), compliance and risk (documents and expiry, questionnaires, risk flags), the relationship view and performance, and the binding of every supplier-facing operation to its supplier.

### 2.2 Out of scope in version 1 (`[Later]`)

From the archived PRD's list: supplier risk scoring from external data; multi-tier supplier collaboration.

## 3. Personas

| Persona | Goals in this product | Surface |
|---|---|---|
| Buyer | Qualify suppliers, resolve supplier queries | Enterprise app |
| Approver (manager, budget owner, finance, legal) | Approve suppliers with context, within SLA, or delegate | Enterprise app, email links |
| Procurement administrator | Suppliers configuration, approval configurations | Tenant console (kernel) and enterprise app |
| Supplier primary contact | Register once in the Supplier Network, accept customer connections, maintain one company profile, manage users and bank accounts | Supplier Network portal |

## 4. Domain model

```mermaid
erDiagram
  SUPPLIER ||--o{ SUPPLIER_DOCUMENT : holds
```

`SUPPLIER` is the Business master (BF-101), drawn for context. Questionnaire responses, risk flags, pending changes and performance indicators attach to it; the model is drawn in full after the checkpoint.

## 5. Processes

### 5.1 Supplier onboarding

From the archived PRD's section 5.1: invite (buyer, by email or network identifier) → network matches or creates the supplier organisation → supplier registers once or accepts the connection → customer-specific sections completed (per customer configuration) → duplicate check in the tenant → approval chain (supplier new) → enabled per organisation → published when mandatory documents and sections are complete → supplier available for requests and orders → profile changes offered by the network and accepted per customer rules; customer-side changes run the supplier change chain → documents expire and trigger actions → hold, end relationship or deactivate when needed (warned when open documents exist).

## 6. Requirements

Each requirement has an identifier, a priority (Must, Should, Could), a tag, the requirement statement in business terms, the reason, acceptance criteria that a tester can run (including at least one error case), dependencies and notes.

### 6.1 Onboarding (SUP-001 to SUP-020)

#### SUP-001 Supplier invitation
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Onboarding

**Requirement:** A buyer with the invite permission shall invite a supplier (company name, country, primary contact email or network identifier, optional category and documents to sign), creating a supplier in Invited status in the tenant and a relationship invitation in the Supplier Network, which matches an existing network organisation by tax identifier or email domain or sends a time-limited registration link (72 hours, resendable); duplicates in the tenant shall be flagged before sending.

**Why:** Start of onboarding.

**Acceptance criteria:**

- AC1 Given a new company and contact, When invited, Then the supplier exists as Invited and the email is sent.
- AC2 Given a company that matches an existing supplier by tax identifier or name similarity, When invited, Then a duplicate warning lists the matches and requires confirmation. (error case)
- AC3 Given an expired link, When resent, Then a new link is sent and the old one stops working.
- AC4 Given the supplier already exists in the network, When invited, Then the existing organisation receives a connection request instead of a registration link. (edge)

*Depends on:* TF-018, TF-232

**Notes:** Was FF-020.

#### SUP-002 Supplier self-registration
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Onboarding

**Requirement:** A new supplier shall register once in the Supplier Network (credentials, MFA, terms, network profile: legal name, country, tax identifiers, one contact); an existing supplier shall accept the connection; then the customer-specific wizard with sections enabled per tenant (addresses, contacts, sites, bank accounts, tax and compliance, documents, questionnaires) pre-fills from the network profile, saves progress and submits when mandatory sections are complete.

**Why:** Supplier self-service.

**Acceptance criteria:**

- AC1 Given mandatory sections complete, When submitted, Then the supplier moves to Registered and the buyer is notified.
- AC2 Given a mandatory document missing, When submitted, Then the section and document are named and submission is refused. (error case)
- AC3 Given progress saved, When the contact returns, Then the wizard resumes where they left.

*Depends on:* SUP-001, TF-028, TF-164

**Notes:** Was FF-021.

#### SUP-003 Duplicate supplier detection
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Onboarding

**Requirement:** On create and on registration the system shall check exact matches (tax identifiers, registration numbers, email, phone, bank account) and fuzzy matches (name and address similarity above a configurable threshold) and show candidates; a buyer may mark a candidate as "not a duplicate" with a reason.

**Why:** Duplicate suppliers break spend analysis and payments.

**Acceptance criteria:**

- AC1 Given a name similar above the threshold, When saved, Then candidates are shown and confirmation required.
- AC2 Given an exact tax identifier match, When saved, Then it is refused. (error case)
- AC3 Given a candidate marked not duplicate, When saved again, Then it is not shown again for that pair.

*Depends on:* BF-101

**Notes:** Was FF-023.

#### SUP-004 Supplier approval and publishing
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Onboarding

**Requirement:** New suppliers shall run the supplier-new approval configuration; approved suppliers shall be published only when publishing criteria are met (mandatory documents valid, required sections complete, at least one active contact, bank account verified when required); unpublishing shall remove the supplier from selection on new documents.

**Why:** Only vetted suppliers receive orders.

**Acceptance criteria:**

- AC1 Given criteria met, When published, Then the supplier can be selected on requests and orders.
- AC2 Given an expired mandatory document, When publishing, Then it is refused naming the document. (error case)
- AC3 Given unpublished, When a new request line is edited, Then the supplier is not offered and existing documents keep it.

*Depends on:* BF-101, TF-090

**Notes:** Was FF-024.

#### SUP-005 Supplier onboarding configuration
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Onboarding

**Requirement:** Settings for enabled registration sections, required documents and questionnaires, bank verification rules, duplicate thresholds, portal channels and supplier defaults.

**Why:** Different onboarding per tenant.

**Acceptance criteria:**

- AC1 Given a section disabled, When a supplier registers, Then it is not shown.
- AC2 Given bank verification required, When a bank change is approved without checklist, Then it is refused. (error case)
- AC3 Given a threshold changed, When duplicate detection runs, Then the new threshold applies.

*Depends on:* TF-057, SUP-002

**Notes:** Was FF-171.

### 6.2 Changes and status (SUP-021 to SUP-040)

#### SUP-021 Supplier status control
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Changes and status

**Requirement:** Permitted users shall put a supplier on hold, release, deactivate and reactivate with a mandatory reason; hold and deactivation shall warn when open orders, receipts in approval or submitted invoices exist and shall require a second confirmation; on hold or inactive suppliers shall not be selectable and their portal users shall see a status notice.

**Why:** Carried; adds guards and reasons.

**Acceptance criteria:**

- AC1 Given open orders, When deactivation is requested, Then the list of open documents is shown and confirmation is required.
- AC2 Given a supplier on hold, When a buyer creates an order for it, Then it is refused. (error case)
- AC3 Given reactivation, When done, Then the previous published status is restored.

*Depends on:* SUP-004

**Notes:** Was FF-025.

#### SUP-022 Supplier change approval
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Changes and status

**Requirement:** Changes to approved suppliers (by supplier or buyer) in controlled fields (legal name, tax identifiers, bank, addresses, contacts) shall run the supplier-change approval configuration; uncontrolled fields apply immediately; pending changes shall be visible side by side with current values.

**Why:** Control over master changes.

**Acceptance criteria:**

- AC1 Given a controlled field changed, When saved, Then the record keeps current values until approval and shows the pending change.
- AC2 Given rejection, When done, Then the change is discarded and the proposer notified.
- AC3 Given an uncontrolled field changed, When saved, Then it applies at once with history. (edge)
- AC4 Given a change to a controlled field by a user without permission, When saved, Then it is refused. (error case)

*Depends on:* BF-101, TF-090

**Notes:** Was FF-033.

#### SUP-023 Supplier bank account verification
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Changes and status

**Requirement:** Any addition or change of a supplier's bank account shall run an approval with a mandatory out-of-band verification checklist (call-back to a known number, document check) recorded by the approver; changes shall notify the supplier's primary contact and the buyer owner.

**Why:** Bank account change fraud is the main payables risk.

**Acceptance criteria:**

- AC1 Given a bank change by a supplier user, When submitted, Then the approver sees the checklist and cannot approve until it is completed.
- AC2 Given a change approved without the checklist, When attempted, Then it is refused. (error case)
- AC3 Given a change, When saved, Then the primary contact and the buyer owner are notified.

*Depends on:* BF-105, TF-090

**Notes:** Was FF-029, the verification; the account data is BF-105. Decision F-05 confirmed.

#### SUP-024 Supplier merge
*Priority:* **Could** · *Tag:* `[New]` · *Area:* Changes and status

**Requirement:** A permitted user shall merge a duplicate supplier into a surviving supplier, moving documents, contacts and portal users, with a preview and an audit of the merge; the merged supplier becomes inactive with a pointer to the survivor.

**Why:** Clean master data.

**Acceptance criteria:**

- AC1 Given a merge preview, When confirmed, Then all documents reference the survivor and the duplicate is inactive.
- AC2 Given a merge of suppliers with different tax identifiers, When attempted, Then a warning requires confirmation. (edge)
- AC3 Given a merge, When audited, Then the full list of moved records is recorded.
- AC4 Given a merge requested by a user without the merge permission, When attempted, Then it is refused. (error case)

*Depends on:* SUP-003

**Notes:** Was FF-037.

### 6.3 Compliance and risk (SUP-041 to SUP-060)

#### SUP-041 Supplier documents and compliance expiry
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Compliance and risk

**Requirement:** Document types (name, issuer type, mandatory for publishing, expiry rule, expiry action: warn, block new orders, unpublish) shall be configured; supplier documents carry issue and expiry dates and attachments; a daily job shall apply expiry actions and notify the supplier and buyer 30, 14 and 1 days before expiry.

**Why:** Compliance at all times.

**Acceptance criteria:**

- AC1 Given a document expiring in 14 days, When the job runs, Then supplier and buyer are notified.
- AC2 Given an expired document with action "block new orders", When an order is created, Then it is refused naming the document. (error case)
- AC3 Given a renewed document uploaded, When saved, Then the block is lifted.

*Depends on:* BF-101, TF-120, TF-150

**Notes:** Was FF-030.

#### SUP-042 Supplier questionnaires
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Compliance and risk

**Requirement:** Buyers shall send questionnaires (kernel forms) to suppliers for qualification and periodic review; responses shall be scored by evaluators and stored on the supplier with dates; questionnaires may be required for publishing.

**Why:** Structured qualification.

**Acceptance criteria:**

- AC1 Given a required questionnaire unanswered, When publishing, Then it is refused. (error case)
- AC2 Given a response scored, When viewed on the supplier, Then score and date are shown.
- AC3 Given a questionnaire sent, When the supplier logs in, Then it appears in their portal with status. (edge)

*Depends on:* BF-101

**Notes:** Was FF-031. AC1 is marked as the error case it is (ADR-0041). Kernel forms are the kernel's forms engine, TF-242, a capability to be added.

#### SUP-043 Supplier risk flags
*Priority:* **Could** · *Tag:* `[New]` · *Area:* Compliance and risk

**Requirement:** Buyers shall record risk flags on suppliers (type, severity, source, date, note) that show on supplier selection and may block new orders when severity is critical.

**Why:** Manual risk management before any external data source.

**Acceptance criteria:**

- AC1 Given a critical flag, When an order is created, Then it is refused with the flag shown. (error case)
- AC2 Given a medium flag, When a supplier is selected, Then a warning is shown.
- AC3 Given a flag cleared, When saved, Then the history keeps it with the clearing reason.

*Depends on:* BF-101

**Notes:** Was FF-039.

### 6.4 Relationship and performance (SUP-061 to SUP-080)

#### SUP-061 Supplier 360 view and lists
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Relationship and performance

**Requirement:** Buyers shall see a supplier page with tabs (profile, addresses, contacts, sites, banks, documents, questionnaires, performance, orders, receipts, invoices, contracts, catalogue, approvals, history) and lists with filters by status, category, country and document expiry.

**Why:** One place for the relationship.

**Acceptance criteria:**

- AC1 Given a supplier, When opened, Then all tabs load with counts.
- AC2 Given a filter by documents expiring within 30 days, When applied, Then only matching suppliers appear.
- AC3 Given a user without read permission, When opened, Then it is refused. (error case)

*Depends on:* BF-101

**Notes:** Was FF-034.

#### SUP-062 Supplier performance indicators
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Relationship and performance

**Requirement:** The system shall compute per supplier and period: on-time acknowledgement rate, on-time delivery rate (receipt date versus need-by), receipt rejection rate, invoice accuracy (invoices held or rejected), and show them on the supplier and in reports.

**Why:** Supplier management needs facts.

**Acceptance criteria:**

- AC1 Given orders and receipts in a quarter, When indicators compute, Then the rates match the underlying documents.
- AC2 Given no activity, When viewed, Then indicators show "no data" not zero. (edge)
- AC3 Given an indicator below a threshold, When evaluated, Then the supplier is flagged for review.
- AC4 Given a user without the supplier performance permission, When indicators are requested, Then it is refused. (error case)

*Depends on:* ORD-021, RCV-024, INV-022

**Notes:** Was FF-032. Decision F-09 confirmed.

#### SUP-063 Supplier reports
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Relationship and performance

**Requirement:** Supplier master reports (status, categories, documents expiring, pending changes, bank verifications) and performance indicators per period.

**Why:** Supplier management.

**Acceptance criteria:**

- AC1 Given documents expiring within 30 days, When the report runs, Then suppliers and documents are listed.
- AC2 Given performance indicators, When run for a quarter, Then rates per supplier are shown.
- AC3 Given export, When run, Then a file is produced. (edge)
- AC4 Given a user without the supplier report permission, When run, Then it is refused. (error case)

*Depends on:* SUP-062, SUP-061

**Notes:** Was FF-163.

### 6.5 Supplier access (SUP-081 to SUP-100)

#### SUP-081 Supplier portal ownership
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Supplier access

**Requirement:** Every supplier-facing operation shall arrive from the Supplier Network with a delegated token bound to one supplier relationship and shall be bound to that supplier by a kernel record policy; lists filter to the supplier; access by identifier to another supplier's record is refused and audited with the supplier user identity.

**Why:** Gap: missing ownership checks in the current product.

**Acceptance criteria:**

- AC1 Given supplier A's user requests supplier B's order, When evaluated, Then it is refused and audited. (error case)
- AC2 Given a list of invoices, When loaded by a supplier user, Then only their invoices appear with correct paging.
- AC3 Given a buyer impersonating a supplier user, When acting, Then the supplier scope applies and the audit shows both identities.

*Depends on:* TF-035, TF-025

**Notes:** Was FF-036.

## 7. Supplier views

What the supplier sees and does in the portal for this product; the narrative is written after the checkpoint. The requirements (SUP-101 to SUP-120):

#### SUP-101 Company data in the portal
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Supplier views

**Requirement:** The primary contact maintains one company profile in the network (company data, addresses, contacts, bank accounts, documents) and customer-specific data (sites, questionnaires, customer-required documents) per customer; profile changes are offered to every connected customer and each applies its approval and verification rules; a per-customer pending indicator shows what awaits approval and what was declined.

**Why:** Current data without buyer effort.

**Acceptance criteria:**

- AC1 Given a bank change in the network profile, When submitted, Then every connected customer shows a pending change with its own verification checklist.
- AC2 Given a non-primary contact, When they try to change bank data, Then it is refused. (error case)
- AC3 Given a document expiring for customer A, When the supplier logs in, Then a reminder names customer A.

*Depends on:* SUP-023, SUP-041, SUP-022, TF-235

**Notes:** Was FF-153.

#### SUP-102 Questionnaires and documents in the portal
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier views

**Requirement:** Suppliers answer questionnaires and upload required documents with expiry dates; the portal shows what is missing for publishing.

**Why:** Onboarding completeness.

**Acceptance criteria:**

- AC1 Given a missing mandatory document, When the home page loads, Then it is listed as a to-do.
- AC2 Given a questionnaire submitted, When viewed, Then it is read-only with status.
- AC3 Given an expired document, When replaced, Then the block lifts. (edge)
- AC4 Given a document uploaded with an expiry date in the past, When saved, Then it is refused. (error case)

*Depends on:* SUP-041, SUP-042

**Notes:** Was FF-157.

## 8. Rules and state machines

### 8.1 Key rules

| Rule | Statement |
|---|---|
| Ownership | Supplier users see and act only on records of their supplier; enforced by kernel record policies on every operation. |

### 8.2 State machines

#### Supplier

```mermaid
stateDiagram-v2
  [*] --> Invited
  Invited --> Registered: registration complete
  Registered --> InApproval: submit
  InApproval --> Approved
  InApproval --> Rejected
  Rejected --> Registered: edit
  Approved --> Published: publish (criteria met)
  Published --> Unpublished
  Unpublished --> Published
  Published --> OnHold
  Approved --> OnHold
  OnHold --> Published: release
  Published --> Inactive: deactivate
  Inactive --> Approved: reactivate
```

## 9. Records and signatures (Part 11)

Which records are approved, acknowledged or signed and so need electronic signatures (TF-063, TF-219), which need an audit-trail review, and their retention class (TF-066). Written after the checkpoint.

## 10. Visibility tags

How this product's records take their tags (TF-241, rule 6). Written after the checkpoint.

## 11. Data, integration and reports

### 11.1 Data notes

The bank account and tax identifiers of the supplier record use field-level encryption (TF-074, in the Business PRD's section 8.1).

### 11.2 Integrations

| Direction | Data | Trigger | Kernel capability |
|---|---|---|---|
| Outbound | Approved suppliers and bank changes | Events | Webhooks, outbound connectors (TF-105, TF-114) |

### 11.3 Reports

From the archived PRD's section 8.4: supplier master with status and documents expiring (SUP-063).

### 11.4 Search, exports and analytics feed

What this product indexes for search (TF-204), exports for audit and the ERP (TF-203, TF-205, TF-206) and feeds to analytics (TF-089, TF-203), as the archived FF-164 to FF-166 asked. Declared after the checkpoint.

## 12. Dependencies

The business and kernel requirements this product needs, by identifier. Written after the checkpoint.

## 13. Layer, module, schema and permissions

Written after the checkpoint. The proposed module is `Iron.Products.SupplierManagement` (schema `products_supplier_management`), not `Iron.Products.Suppliers`: the supplier master would be `Iron.Business.Suppliers`, and two modules whose names end in the same segment cannot both be named to `make ef-add MODULE=` (ADR-0029).

The archived PRD's permission catalogue (its section 8.3) named these for the life cycle: `suppliers.supplier.{approve,publish,hold,deactivate,invite,impersonate}`, `suppliers.bank.verify`, `suppliers.document.*`. Permission names name the module, so they follow it once it is settled.

## 14. Configuration

From the archived PRD's section 8.5: supplier onboarding sections and required documents; duplicate detection thresholds.

## 15. Slices

Where the moved requirements sat in the archived PRD's build plan (its section 9). The product slices are proposed in the technical PRD's section 7 after the checkpoint.

| Slice of the archived plan | Its outcome | This document's requirements from it |
|---|---|---|
| P2 | Suppliers and orders | SUP-001, SUP-002, SUP-003, SUP-004, SUP-005, SUP-021, SUP-022, SUP-023, SUP-041, SUP-042, SUP-061, SUP-081, SUP-101 |
| P6 | Completeness | SUP-024, SUP-043, SUP-062, SUP-063, SUP-102 |

## 16. Decisions and open points

### 16.1 Decisions

Confirmed on 2026-10-17, from the archived PRD's section 10.1:

| ID | Decision | Where applied |
|---|---|---|
| F-05 | Supplier bank changes need a second approver and an out-of-band verification checklist | SUP-023, BF-105 |
| F-09 | Supplier performance indicators: on-time acknowledgement, on-time delivery, receipt rejections, invoice accuracy | SUP-062 |

### 16.2 Open points

| Question | Default proposed |
|---|---|
| SUP-081 (portal ownership, was FF-036) binds every supplier-facing operation to its supplier, in every product, while it lives here. | Each product declares its own record policy for supplier users (TF-035) and cites SUP-081 |

## 17. Glossary

The terms of the supplier master are in the Business PRD's glossary, the terms of the network in the supplier portal surface's.
