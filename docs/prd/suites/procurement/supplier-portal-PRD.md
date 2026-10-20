# eProcurement Suite: Supplier portal and Supplier Network experience PRD

## 0. Document control

| Item | Value |
|---|---|
| Document title | The supplier portal and the Supplier Network experience: one portal for all suppliers of all customers, Product Requirements Document of a surface |
| Layer | A surface of the eProcurement Suite, on IronSaaS Foundation, not a product (ADR-0029, ADR-0043): the supplier's screens are served through the kernel's Supplier Network plane (TF-230 to TF-239), and each product contributes its supplier views in its own PRD's section 7 |
| Identifier prefix | `SPX-`, in blocks of twenty per area (ADR-0041); the table is `supplier-portal-requirements.csv` |
| Version | 1.1 (accepted at the review of 2026-10-20; moved by ADR-0043) |
| Date | 2026-10-20 |
| Status | Accepted at the design session's review of 2026-10-20, its decisions written into the requirements and marked "decided 2026-10-20" in their notes. |
| Inputs | The Functional Foundation PRD for procure-to-pay, version 1.2 (archived as `../../archive/functional-foundation-p2p-PRD-v1.md`), the map (`../../requirements-map.md`), the Technical Foundation PRD's section 3.6, ADR-0029 and ADR-0041. |
| Dependency | The network itself (identity, registry, relationships, action index, delegated access, profile propagation) is the kernel's TF-230 to TF-239; the supplier record is the Business PRD's; the documents are the products'. |
| Tags | `[Carried]` behaviour from the current product that stays. `[Improved]` carried but changed to close a defect or gap. `[New]` not in the current product, standard practice. `[Decision]` needs the owner's confirmation; the question is in its notes. `[Later]` recorded, not in version 1. A requirement moved from the archived PRD says what it was at the start of its notes. |

Change log: 1.1, 2026-10-20: moved to `docs/prd/suites/procurement/` as the eProcurement Suite's surface on IronSaaS Foundation (ADR-0043), beside the products whose supplier views it composes; no requirement changed. 1.0, 2026-10-20: accepted at the review; its decisions are written into the requirements, and the proposals of the split's sections stand. 0.2 DRAFT, 2026-10-20: the design session's answers at the checkpoint and the split's sections (supplier views, records and signatures, visibility tags, search and exports, dependencies by ADR-0042, module and permissions). 0.1 SKELETON, 2026-10-20: the split (ADR-0041). The supplier portal's own screens and the Supplier Network experience moved here from the Functional Foundation PRD 1.2, their words unchanged except for names that changed. The supplier views of each product moved to that product.

## 1. Vision and goals

### 1.1 Vision

A supplier registers once, works with every customer from one portal, and finds every order, notice, invoice and query across customers in one inbox, while every customer's documents and rules stay in that customer's tenant.

### 1.2 Goals (outcomes)

Goals 3 and 8 of the archived PRD, in its words:

1. Suppliers self-serve: onboarding, order acknowledgement, shipment notices, invoices and catalogues, with a target of 70% of orders acknowledged in the portal within 3 days.
2. A supplier registers once, works with every customer from one portal, and finds every order, notice, invoice and query across customers in one inbox.

### 1.3 Lessons from the current product

Keep, from the archived PRD's list: supplier portal for orders, shipment notices, invoices and catalogues. Improve: supplier ownership on every portal operation (SPX-006); one rule implementation for both surfaces.

## 2. Scope

### 2.1 The Supplier Network: one portal for all suppliers

From the archived PRD's section 2.3. The supplier side is not a screen set inside each customer's tenant. It is the Supplier Network, one portal for all suppliers of all customers (Technical Foundation section 3.6, TF-230 to TF-239). What this means for the products:

- A supplier registers once in the network. A customer invites the supplier; the network matches or creates the supplier organisation; the supplier accepts the connection. Inside the customer's tenant the supplier record is linked to the network organisation and enabled per organisation (BF-108).
- The supplier maintains one profile (company data, addresses, contacts, bank accounts, documents). Each customer accepts proposed changes through its own approval and verification rules. Customer-specific values (terms, status, preferred flag) stay with the customer.
- Orders, shipment notices, receipts, invoices, credit notes, contracts, questionnaires and tasks live only in the customer's tenant. The network keeps a minimal action index so the supplier sees one inbox across customers and opens any item in the customer's context with a delegated token.
- Supplier screens are portal views served through the network: order and acknowledgement, shipment notice, receipt view, invoice entry with validations, catalogue upload, questionnaires, tasks, company data. They look the same for every customer, show the customer's name, organisation and terms, and apply the customer's rules.
- Buyers invite, enable, hold and end relationships from the tenant; they see only their own relationship with the supplier.

Requirements SPX-021 to SPX-028 define the supplier experience; SPX-001 to SPX-005 are the portal's own screens, and each product's supplier views are in its PRD (section 4).

### 2.2 Out of scope in version 1 (`[Later]`)

From the archived PRD's list: multi-tier supplier collaboration. Supplier single sign-on is a later slice (TF-230, open point O-10 of the technical PRD).

## 3. Personas

| Persona | Goals in this surface | Surface |
|---|---|---|
| Supplier primary contact | Register once in the Supplier Network, accept customer connections, maintain one company profile, manage users and bank accounts, upload catalogues per customer | Supplier Network portal |
| Supplier contact | Work one inbox across customers: acknowledge orders, send shipment notices, submit invoices, answer queries and questionnaires | Supplier Network portal |

## 4. How the surface is composed

The portal's own screens are this document's (section 5). Every other supplier screen is a view a product contributes, specified in that product's section 7. Every view obeys the portal's rules (section 6), supplier ownership first (SPX-006); a view cites the rule and depends on the kernel requirements that enforce it, TF-035 and TF-234, not on this surface (ADR-0042):

| Supplier view | Requirement | Product |
|---|---|---|
| Orders and acknowledgement | ORD-101 | `orders-PRD.md` |
| Shipment notices | RCV-121 | `receiving-PRD.md` |
| Receipts and returns | RCV-122 | `receiving-PRD.md` |
| Invoices | INV-101 | `invoicing-PRD.md` |
| Catalogue | CAT-061 | `catalogue-PRD.md` |
| Company data | SUP-101 | `supplier-management-PRD.md` |
| Questionnaires and documents | SUP-102 | `supplier-management-PRD.md` |
| Contracts | CON-061 | `contracts-PRD.md` |

## 5. Requirements

Each requirement has an identifier, a priority (Must, Should, Could), a tag, the requirement statement in business terms, the reason, acceptance criteria that a tester can run (including at least one error case), dependencies and notes.

### 5.1 Portal (SPX-001 to SPX-020)

#### SPX-001 Supplier portal access through the network
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Portal

**Requirement:** Supplier users log in once to the Supplier Network (password, MFA; SSO later), see a customer switcher and a unified inbox, and open each customer's screens served with that customer's name, branding context, organisation and rules; inside a customer they see only their supplier's records (SPX-006) and only modules the customer enabled for suppliers.

**Why:** Secure self-service.

**Acceptance criteria:**

- AC1 Given a supplier user connected to two customers, When logged in, Then the inbox shows both customers' items and the switcher lists both.
- AC2 Given an enterprise user, When they open the network portal, Then it is refused. (error case)
- AC3 Given customer A disables supplier invoicing, When the supplier works in A, Then invoices are hidden for A only.

*Depends on:* TF-230, TF-233, TF-234, TF-040, SPX-006

**Notes:** Was FF-150.

#### SPX-002 Portal home
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Portal

**Requirement:** A home page with counts of orders awaiting acknowledgement, notices due, invoices by status, held or disputed invoices, documents expiring, open tasks and announcements.

**Why:** Guide the supplier.

**Acceptance criteria:**

- AC1 Given data, When loaded, Then counts are correct and link to lists.
- AC2 Given no data, When loaded, Then zeros are shown. (edge)
- AC3 Given an announcement for suppliers, When published, Then it appears.
- AC4 Given a supplier user whose supplier is inactive, When the home page loads, Then a status notice is shown and actions are disabled. (error case)

*Depends on:* SPX-001

**Notes:** Was FF-159.

#### SPX-003 Tasks and queries in the portal
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Portal

**Requirement:** Suppliers see tasks assigned to them (kernel tasks), respond, attach files and resolve; buyers reopen or close.

**Why:** Tracked communication.

**Acceptance criteria:**

- AC1 Given a task assigned, When the supplier resolves it, Then the buyer is notified.
- AC2 Given a closed task, When the supplier tries to respond, Then it is refused. (error case)
- AC3 Given a task with an attachment, When opened, Then the file is downloadable.

*Depends on:* TF-097

**Notes:** Was FF-158. The tasks are the kernel's (TF-097) when they exist.

#### SPX-004 Supplier portal across organisations
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Portal

**Requirement:** A supplier user shall see documents, tasks and questionnaires from every organisation of the tenant that enabled the supplier, grouped or filterable by organisation, with the organisation's name, terms and contacts shown on each document; data of organisations that did not enable the supplier is never shown.

**Why:** One portal for the supplier, clear separation for the buyer group.

**Acceptance criteria:**

- AC1 Given enablement in A and B, When the supplier opens orders, Then orders of A and B appear with the organisation shown.
- AC2 Given no enablement in C, When the supplier opens a C document by identifier, Then it is refused. (error case)
- AC3 Given an invoice created by the supplier, When saved, Then its organisation equals the order's organisation and the organisation's bill-to details apply.

*Depends on:* BF-108, SPX-006, SPX-001

**Notes:** Was FF-189.

#### SPX-005 Supplier portal users
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Portal

**Requirement:** A supplier contact with portal access is a Supplier Network user of the supplier organisation, created by the primary contact in the network; buyers may block a network user for their tenant only.

**Why:** Portal access control.

**Acceptance criteria:**

- AC1 Given a new network user added by the primary contact, When saved, Then the user can act for every customer per role and appears as a contact in each connected tenant.
- AC2 Given an email already used in the network, When saved, Then it is refused. (error case)
- AC3 Given a tenant blocks a network user, When that user opens the tenant's items, Then they are refused for that tenant only and other customers are unaffected.

*Depends on:* BF-103, TF-230, TF-239

**Notes:** Was FF-027, the portal users; the contact data is BF-103.

#### SPX-006 Supplier portal ownership
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Portal

**Requirement:** Every supplier-facing operation shall arrive from the Supplier Network with a delegated token bound to one supplier relationship and shall be bound to that supplier by a kernel record policy; lists filter to the supplier; access by identifier to another supplier's record is refused and audited with the supplier user identity.

**Why:** Gap: missing ownership checks in the current product.

**Acceptance criteria:**

- AC1 Given supplier A's user requests supplier B's order, When evaluated, Then it is refused and audited. (error case)
- AC2 Given a list of invoices, When loaded by a supplier user, Then only their invoices appear with correct paging.
- AC3 Given a buyer impersonating a supplier user, When acting, Then the supplier scope applies and the audit shows both identities.

*Depends on:* TF-035, TF-025

**Notes:** Was FF-036. Moved from Supplier management (SUP-081 in the checkpoint's skeleton, an identifier not reused) by the design session's answer: a rule every product's supplier views obey (section 4); they cite it and depend on the kernel's TF-035 and TF-234 (ADR-0042).

### 5.2 Supplier Network experience (SPX-021 to SPX-040)

These rest on the kernel's Supplier Network plane, TF-230 to TF-239.

#### SPX-021 Supplier connection and onboarding through the network
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** A customer's invitation shall create a connection request in the Supplier Network; a new supplier registers once (network profile and credentials) and an existing supplier accepts the request; the connection becomes a relationship linked to the customer's supplier record; the customer-specific onboarding wizard (SUP-002) then runs with the network profile pre-filled.

**Why:** Decision F-16: one registration for all customers.

**Acceptance criteria:**

- AC1 Given an existing network supplier invited by a second customer, When they accept, Then no new registration is needed and the second customer's wizard opens with the profile pre-filled.
- AC2 Given a new supplier, When they complete network registration, Then the first customer's wizard opens next.
- AC3 Given an invitation to a tax identifier that belongs to a different registered organisation, When sent, Then the network flags the conflict to the buyer before the invitation goes out. (error case)

*Depends on:* SUP-001, SUP-002, TF-231, TF-232

**Notes:** Was FF-190.

#### SPX-022 Unified inbox across customers
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** The supplier portal shall show one inbox of items needing action across all customers (orders awaiting acknowledgement, shipment notices due, receipts to review, invoices held or disputed, questionnaires, tasks, documents expiring) with the customer name and organisation on each item, filters by customer, type, status and due date, sorting by due date, and a count per customer.

**Why:** Suppliers work for many customers; one list saves time.

**Acceptance criteria:**

- AC1 Given items from three customers, When the inbox loads, Then all appear with customer and organisation names and due dates.
- AC2 Given a filter by customer B, When applied, Then only B's items appear.
- AC3 Given an item whose customer unit is unreachable, When the inbox loads, Then it is marked unreachable and can be retried. (error case)

*Depends on:* TF-233

**Notes:** Was FF-191.

#### SPX-023 Customer switcher and customer context
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** A customer switcher shall set the active customer for all screens (orders, notices, receipts, invoices, catalogue, questionnaires, tasks, company data per customer); screens show the customer's name, organisation, terms and rules; actions run in the customer's tenant with the customer's validations.

**Why:** Clear context prevents mistakes across customers.

**Acceptance criteria:**

- AC1 Given the switcher set to customer A, When orders are listed, Then only A's orders appear with A's branding context.
- AC2 Given an invoice created under customer A, When submitted, Then A's matching rules and tolerances apply.
- AC3 Given a user without a relationship to customer C, When they try to switch to C, Then it is refused. (error case)

*Depends on:* TF-234, SPX-001

**Notes:** Was FF-192.

#### SPX-024 Acting on documents of several customers
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** Supplier users shall acknowledge orders, submit shipment notices and invoices, answer questionnaires and resolve tasks for any connected customer from the portal; bulk acknowledgement across customers shall run each item in its customer's tenant and report per item.

**Why:** The purpose of one portal.

**Acceptance criteria:**

- AC1 Given orders from two customers selected, When bulk acknowledged, Then each is acknowledged in its tenant and the results list shows both.
- AC2 Given one customer rejects an acknowledgement (order cancelled meanwhile), When the bulk action runs, Then the others succeed and the failure shows the customer's reason. (error case)
- AC3 Given an invoice submitted for customer A, When saved, Then it exists only in A's tenant and the inbox item closes.

*Depends on:* TF-236, ORD-021, RCV-001, INV-002

**Notes:** Was FF-193.

#### SPX-025 One profile, per-customer acceptance
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** The supplier shall maintain one network profile; each change shall be offered to every connected customer; the supplier shall see per customer whether a change is pending, accepted or declined with the customer's reason; customer-specific data (terms, status, preferred flag, questionnaires, customer-required documents) stays per customer.

**Why:** Less duplicate maintenance for suppliers, full control for customers.

**Acceptance criteria:**

- AC1 Given an address change, When saved, Then each customer shows pending until its approval completes.
- AC2 Given customer B declines with a reason, When viewed, Then the reason is shown for B only.
- AC3 Given a change rejected by a customer's validation (for example an unsupported bank code format), When received, Then the supplier sees the validation message for that customer. (error case)

*Depends on:* TF-235, SUP-023, SUP-022

**Notes:** Was FF-194.

#### SPX-026 Supplier users and roles in the network
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** The supplier's primary contact shall manage the supplier organisation's users (invite, roles, remove) in the network; roles decide which customers and which document types a user may work (all customers by default, or a customer subset); customers may block a user for their tenant only.

**Why:** Supplier organisations need their own user administration.

**Acceptance criteria:**

- AC1 Given a user limited to customers A and C, When they log in, Then B's items are not shown.
- AC2 Given a user removed by the primary contact, When they try to log in, Then it is refused and sessions end. (error case)
- AC3 Given customer B blocks a user, When that user opens B's items, Then they are refused for B only.

*Depends on:* TF-230, TF-239, SPX-005

**Notes:** Was FF-195.

#### SPX-027 Supplier notifications across customers
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** Supplier users shall receive notifications from all customers through the network with the customer named, choose channels and digests once for all customers, and never receive a customer's internal notifications; customers keep their templates and branding in the content.

**Why:** One mailbox, clear sender context.

**Acceptance criteria:**

- AC1 Given orders issued by customers A and B, When the daily digest is chosen, Then one email lists both grouped by customer.
- AC2 Given a customer's internal notification key, When evaluated, Then it is never routed to supplier users. (error case)
- AC3 Given an immediate channel chosen for holds, When a hold occurs, Then the notification is sent at once with the customer named.

*Depends on:* TF-236, TF-151

**Notes:** Was FF-196.

#### SPX-028 Relationship status and history
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Supplier Network experience

**Requirement:** Suppliers shall see each customer relationship with status (connected, on hold, ended), the customer's enabled organisations, terms and performance indicators the customer chooses to share; ended relationships keep read-only history of documents for the retention period.

**Why:** Transparency for suppliers.

**Acceptance criteria:**

- AC1 Given a relationship on hold, When viewed, Then the status and the customer's message are shown and actions are disabled.
- AC2 Given a customer sharing on-time delivery, When viewed, Then the indicator is shown for that customer only.
- AC3 Given an ended relationship, When the supplier opens a document, Then it is read-only and no action is offered. (error case)

*Depends on:* TF-232, SUP-062

**Notes:** Was FF-197.

## 6. Rules

From the archived PRD's section 7.1:

| Rule | Statement |
|---|---|
| Network boundary | Supplier credentials and the supplier's master profile live in the Supplier Network; documents live only in the customer tenant; every supplier action arrives with a delegated token for one customer and one relationship and is audited with the supplier user identity. |
| Ownership | Supplier users see and act only on records of their supplier; enforced by kernel record policies on every operation (SPX-006). Every product's supplier views obey it. |

## 7. Data and integration

| Direction | Data | Trigger | Kernel capability |
|---|---|---|---|
| Outbound | Action index events for suppliers (orders issued, holds, disputes, tasks, questionnaires) with the tenant's configured fields | Document events | Supplier Network (TF-233) |
| Inbound | Supplier actions and profile change proposals from the network with delegated tokens | Supplier action | Supplier Network (TF-234, TF-235) |

## 8. Dependencies

The surface sits above the products (ADR-0042): it may use any of them, and none depends on it.

- Kernel: TF-025 (impersonation), TF-035 (record policies), TF-040 (entitlements, for the modules a customer enabled), TF-097 (tasks), TF-151 (digests), TF-230 to TF-236 and TF-239 (the Supplier Network plane), TF-243 (access in what is sent).
- Business: BF-103 (contacts), BF-108 (enablement per organisation).
- Products: Supplier management (SPX-021 on SUP-001 and SUP-002, SPX-025 on SUP-022 and SUP-023, SPX-028 on SUP-062), Orders (SPX-024 on ORD-021), Receiving (SPX-024 on RCV-001), Invoicing (SPX-024 on INV-002), and every product's supplier views (section 4).

A supplier user acts with the supplier role in a delegated token (TF-234), never with a tenant role; each product's supplier views answer to that role.

## 9. Slices

Where the moved requirements sat in the archived PRD's build plan (its section 9); its supplier-facing parts need the kernel's slice S8 (the technical PRD's section 7).

| Slice of the archived plan | Its outcome | This document's requirements from it |
|---|---|---|
| P2 | Suppliers and orders | SPX-001, SPX-002, SPX-004, SPX-005, SPX-006, SPX-021, SPX-022, SPX-023, SPX-024 |
| P6 | Completeness | SPX-003, SPX-025, SPX-026, SPX-027, SPX-028 |

## 10. Decisions and open points

### 10.1 Decisions

Confirmed on 2026-10-17, from the archived PRD's section 10.1:

| ID | Decision | Where applied |
|---|---|---|
| F-16 | One Supplier Portal for all suppliers of all customers, with easy work across customers | Section 2.1, SPX-021 to SPX-028, and the supplier views of every product, served through the network |

Decided at the PRD split's checkpoint, on 2026-10-20:

| Decision | Where applied |
|---|---|
| Portal ownership moves from Supplier management to this surface, as a rule every product's supplier views obey | SPX-006 (was SUP-081 in the checkpoint's skeleton) and section 6 |
| The surface sits above the products: it depends on them, and their views obey its rules without depending on it (ADR-0042) | Section 4 |

### 10.2 Open points

From the archived PRD's section 10.2:

| ID | Question | Default used |
|---|---|---|
| G-07 | Which supplier profile sections are mandatory at network registration versus per customer? | Network: legal name, country, one contact; customer: everything else per its configuration |

## 11. Glossary

| Term | Meaning |
|---|---|
| Supplier Network | The shared portal and registry where suppliers register once and work with all customers; holds identity, relationships and a minimal action index, never customer documents |
