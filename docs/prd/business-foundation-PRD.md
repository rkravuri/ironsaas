# Foundation: Business Foundation PRD

## 0. Document control

| Item | Value |
|---|---|
| Document title | Business Foundation: the shared business masters and the business configuration of kernel mechanisms, Product Requirements Document |
| Layer | Business (ADR-0029): modules `Iron.Business.<Module>` in `src/Business/`, schemas `business_<module>`; the modules are proposed in section 9 |
| Identifier prefix | `BF-`, in blocks of twenty per area (ADR-0041); the table is `business-foundation-requirements.csv` |
| Version | 0.2 DRAFT for review (the split of 2026-10-20) |
| Date | 2026-10-20 |
| Status | Draft for review. The requirements moved from the Functional Foundation PRD under their new identifiers, the design session's answers at the checkpoint, and the new areas and requirements of the split, marked `[New]`; every rule the sources did not state is a `[Decision]` with its proposed default. |
| Inputs | The Functional Foundation PRD for procure-to-pay, version 1.2 (archived as `archive/functional-foundation-p2p-PRD-v1.md`), the map from its identifiers (`requirements-map.md`), ADR-0029 and ADR-0041, and the split brief (`docs/prompts/PRD-split.md`). |
| Dependency | Every mechanism (identity, roles, the unit tree, the approvals engine, audit, files, notifications, numbering, custom fields and lookups, imports, telemetry) is a kernel capability in the Technical Foundation PRD, cited by its TF identifier and never specified again here. The products build on this document; where a requirement here names a product's, it is an open point (section 12.2). |
| Method | The product method (`product-method.md`) is binding: a process reference pack before the first slice of each module (PM-001). |
| Tags | `[Carried]` behaviour from the current product that stays. `[Improved]` carried but changed to close a defect or gap. `[New]` not in the current product, standard practice. `[Decision]` needs the owner's confirmation; the question is in its notes. `[Later]` recorded, not in version 1. A requirement moved from the archived PRD says what it was at the start of its notes. |

Change log: 0.2 DRAFT, 2026-10-20: the design session's answers at the checkpoint (section 12.1) and the new requirements: kinds and purchasing groups, people, HSN and SAC codes, projects as cost objects, lookups, approval configurations and the delegation of authority matrix, number series and notification templates, visibility tags on masters, bill-to locations, the use of masters by the products, the reconciliation of budgets. 0.1 SKELETON, 2026-10-20: the split (ADR-0041). The requirements of the Functional Foundation PRD 1.2 that every product reads moved here under BF identifiers, their words unchanged except for names that changed (the layers, the kernel modules, the identifiers they cite and the section numbers of this document); the map lists every one.

## 1. Vision and goals

### 1.1 Vision

One set of business masters for every product: the organisations and legal entities, the people, the items and their classification, the finance masters, the locations and the supplier record, with the business configuration of the kernel's mechanisms (approval configurations and the delegation of authority, number series, document templates and notifications, visibility tags on masters). Procure-to-pay uses them first; sourcing, auctions, projects, surveys and expenses will use the same ones. Everything is configurable per tenant and traceable per record.

### 1.2 Goals (outcomes)

Goals 4, 5, 6, 7 and 9 of the archived PRD, in its words:

1. Finance has real-time budget consumption and accruals (received not invoiced) without spreadsheets.
2. Every control (approval limits, tolerances, duplicate checks, segregation of duties) is configuration, audited and provable to an auditor.
3. A new tenant with masters loaded can raise its first request within one day of provisioning.
4. A group of companies runs all its business units (organisations) in one tenant with separated master data, operations and visibility where they want separation, and shared where they want sharing, without any module needing rework later.
5. The product works for the largest global buyers: tens of thousands of users, tens of thousands of suppliers, hundreds of millions of documents over its life, on a dedicated deployment when required.

### 1.3 Lessons from the current product

Keep (carried), from the archived PRD's list: account-string splits with budget periods; budget blocking or warning per document type. Improve: atomic budget checks; warehouse maintenance screen; explicit behaviour for every state transition.

## 2. Scope

### 2.1 What this layer holds

| Area | Holds | Section |
|---|---|---|
| Organisations | Legal entity attributes and the finance masters of a legal entity; business units and departments as kinds of the kernel's unit tree; purchasing groups as a business master; the business rules of organisation separation | 5.1 |
| People | The people master linked to kernel principals: employee identifier, designation, job grade, manager, business groups, defaults | 5.2 |
| Items and classification | Items, categories and commodities, units of measure | 5.3 |
| Finance | Chart of accounts and segments, account strings, budgets, fiscal calendar, currencies and rates, tax codes, payment and shipping terms, projects as cost objects | 5.4 |
| Locations | Ship-to locations, warehouses | 5.5 |
| Supplier master | The supplier record and its parts, which every product reads, and its enablement per organisation | 5.6 |
| Lookups | The business lists every product uses, on the kernel's custom lookups | 5.7 |
| Approvals and delegation of authority | Approval configurations per document type on the kernel's workflow engine, and the delegation of authority matrix | 5.8 |
| Numbering, templates and notifications | Number series, document type settings, document templates, business notification templates | 5.9 |
| Content visibility on masters | Which masters carry visibility tags, on the kernel's TF-241 | 5.10 |
| Setup and data | Starter sets, readiness, country accelerator packs, reference data from standards, masters import, export and ERP sync | 5.11 |

The supplier's life cycle (invitation, registration, approval, status, compliance, performance) is the Supplier management product (`products/supplier-management-PRD.md`); supplier items are the Catalogue product's.

### 2.2 Organisation separation

Indian and Asian customers very often run several business units (they call them organisations) in one company and require separation of master data, operations and data visibility by unit, with sharing where it helps. The kernel built the mechanism in S2 (TF-220 to TF-223) and this layer and the products carry the business rules, so that no module suffers later:

- Every master record and document carries an owning organisation and a sharing mode (private, shared with listed organisations, global) through the kernel pattern (TF-220). The organisation tree, role scopes and the organisation switcher are kernel capabilities (TF-034, TF-222).
- Finance masters: chart of accounts, account strings, budgets and fiscal calendars belong to a legal entity; a legal entity is an organisation with legal attributes; a business unit may sit under a legal entity and use its chart of accounts.
- Suppliers: one supplier master per tenant (so a supplier registers once), enabled per organisation with organisation-specific terms, status and approval; a private supplier is visible only to its owner.
- Items and categories: global by default; organisation-specific items, supplier items and prices allowed; catalogues and contracts published per organisation or shared.
- Documents (requests, orders, receipts, invoices, contracts) belong to the organisation of the requester or buyer; cross-organisation documents are not allowed in version 1 (intercompany is a later feature); visibility follows organisation access.
- Configuration per organisation: numbering, templates, approval configurations, tolerances and settings can override the tenant values (TF-221).
- Reporting: per organisation, and consolidated for group roles with children included.

The archived PRD's area for this was FF-180 to FF-189, and the map says where each went. The kernel covers document visibility by organisation and consolidated reporting (TF-222, TF-035). The business rules are BF-001, BF-002, BF-003, BF-004, BF-005, BF-108, BF-083 and BF-203. Catalogues and contracts per organisation are CAT-007 and CON-006, and the supplier portal across organisations is SPX-004.

### 2.3 Out of scope in version 1 (`[Later]`)

From the archived PRD's list: payment execution and bank integration (ERP); inventory and stock ledger; intercompany documents between organisations of one tenant (the record model keeps hooks: counterpart organisation, intercompany flag, mirrored document reference, so that a request in organisation A fulfilled by organisation B can be added later without rework); translation of business data (Unicode in any language from day one). The rest of that list belongs to the products and is in their scope sections.

## 3. Personas

| Persona | Goals in this layer | Surface |
|---|---|---|
| Controller / finance administrator | Chart of accounts, budgets, fiscal calendar, rates, taxes, tolerances, accruals | Enterprise app |
| Procurement administrator | Suppliers configuration, document templates, numbering, categories, approval configurations | Tenant console (kernel) and enterprise app |
| ERP integration (service account) | Pull approved orders and invoices, push payment status, sync masters | Partner API (kernel) |
| People administrator (HR or administration) | People, designations and job grades, managers, business groups | Tenant console (kernel) and enterprise app |

The kernel provides the identity, roles and permissions for all of them. This layer declares its permission catalogue (section 9) and record policies.

## 4. Domain model

```mermaid
erDiagram
  ORGANISATION ||--o{ ORGANISATION : "parent of"
  ORGANISATION ||--o| LEGAL_ENTITY : "is a (legal attributes)"
  ORGANISATION ||--o{ SUPPLIER_ENABLEMENT : "enables supplier for"
  SUPPLIER ||--o{ SUPPLIER_ENABLEMENT : "enabled in"
  LEGAL_ENTITY ||--o{ CHART_OF_ACCOUNT : owns
  CHART_OF_ACCOUNT ||--o{ SEGMENT : defines
  CHART_OF_ACCOUNT ||--o{ ACCOUNT_STRING : validates
  BUDGET_PERIOD ||--o{ BUDGET_LINE : contains
  BUDGET_LINE }o--|| ACCOUNT_STRING : "for"
  BUDGET_LINE ||--o{ BUDGET_CONSUMPTION : records
  SUPPLIER ||--o{ SUPPLIER_SITE : has
  SUPPLIER ||--o{ SUPPLIER_CONTACT : has
  SUPPLIER ||--o{ SUPPLIER_BANK : has
  ORGANISATION ||--o{ LOCATION : "has (ship-to, bill-to, warehouse)"
  PERSON }o--|| ORGANISATION : "default unit"
  PERSON }o--o| PERSON : "reports to"
  PERSON }o--o{ BUSINESS_GROUP : "member of"
  PURCHASING_GROUP }o--o{ PERSON : "buyers"
  PURCHASING_GROUP }o--o{ CATEGORY : covers
  PURCHASING_GROUP }o--o{ ORGANISATION : serves
  PROJECT }o--|| ORGANISATION : "owned by"
  PROJECT }o--o{ BUDGET_LINE : "consumes"
  AUTHORITY_ROW }o--o| CATEGORY : "for"
  AUTHORITY_ROW }o--|| ORGANISATION : "per legal entity or unit"
```

`ORGANISATION` is the kernel's unit (TF-034), drawn for context. Every master record (supplier, item, category, tax code, payment term, template) also carries an owning organisation and a sharing mode (not drawn). A person is linked to one kernel principal; an authority row of the delegation of authority matrix names a designation, a job grade or a role and a limit. Lookups, approval configurations and templates are configuration and are not drawn.

## 5. Requirements

Each requirement has an identifier, a priority (Must, Should, Could), a tag, the requirement statement in business terms, the reason, acceptance criteria that a tester can run (including at least one error case), dependencies and notes.

### 5.1 Organisations (BF-001 to BF-020)

Legal entity attributes, the finance masters of a legal entity, the business rules of organisation separation (section 2.2), the kinds of the kernel's units, supplied through `IOrganisationKinds` (BF-006), and purchasing groups (BF-007). A purchasing group is a business master, not a unit kind: it serves several units and owns no records. The A1 amendment in `docs/prompts/S2.md` listed it among the kinds; this document corrects that.

#### BF-001 Legal entity finance attributes
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Organisations

**Requirement:** Each legal entity (a kernel organisation unit of the kind legal entity) shall carry finance attributes: entity code, tax registrations, default currency, default chart of account, billing and shipping addresses, numbering initials, logo for documents.

**Why:** Documents belong to a legal entity; numbering and templates depend on it.

**Acceptance criteria:**

- AC1 Given a legal entity without default chart of account, When a request is created for it, Then the user must choose a chart of account and is told why.
- AC2 Given a duplicate entity code, When saved, Then it is refused. (error case)
- AC3 Given numbering initials set, When a document is numbered with the dynamic prefix on, Then the initials appear in the number.

*Depends on:* TF-034, TF-166

**Notes:** Was FF-001.

#### BF-002 Finance masters per legal entity and organisation
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Organisations

**Requirement:** Chart of accounts, account strings, budgets and fiscal calendars belong to a legal entity; an organisation under a legal entity uses that entity's chart of accounts and may have its own budget lines; coding a document to an account string of another legal entity is refused; budgets and consumption are reported per organisation and consolidated.

**Why:** Accounting integrity per legal entity.

**Acceptance criteria:**

- AC1 Given organisation A under legal entity L1, When a request in A is coded, Then only L1 account strings are offered.
- AC2 Given a string of L2, When used on A's document, Then it is refused. (error case)
- AC3 Given budget lines per organisation, When the consolidated report runs, Then totals per organisation and the group total are shown.

*Depends on:* BF-061, BF-064, BF-003

**Notes:** Was FF-185. Decision G-02 default.

#### BF-003 Owning organisation on every record
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Organisations

**Requirement:** Every master record (supplier, item, supplier item, catalogue, contract, category, tax code, payment term, ship-to location, template) and every document (request, order, shipment notice, receipt, return, invoice, credit note) shall carry an owning organisation and a sharing mode through the kernel pattern (TF-220); documents take the organisation of the requester or buyer and never mix organisations; the organisation is shown on lists and records.

**Why:** Decision F-13: separation by business unit from day one.

**Acceptance criteria:**

- AC1 Given a user of organisation A creating a request, When saved, Then the request's organisation is A.
- AC2 Given a request of organisation A, When a line references an item private to organisation B, Then it is refused. (error case)
- AC3 Given a list, When loaded, Then the organisation column is shown and filterable.

*Depends on:* TF-220

**Notes:** Was FF-180. The owning organisation and the sharing mode are the kernel's (TF-220: S1, A1, A3; sharing modes in A8); this requirement keeps the business rules FF-180 adds.

#### BF-004 Sharing modes for master data
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Organisations

**Requirement:** Master data shall support the three sharing modes: global (default for categories, units, currencies, tax codes and payment terms), shared with listed organisations (for example a supplier or item used by two units), and private (visible to the owner only); administrators set the default mode per master type and may change a record's mode with audit.

**Why:** Shared where it helps, separated where required.

**Acceptance criteria:**

- AC1 Given an item shared with organisations A and B, When a user of C searches, Then it is not shown.
- AC2 Given a record changed from global to private, When saved, Then documents of other organisations that already reference it keep working and a warning lists them.
- AC3 Given a change by a user without the master's configuration permission, When attempted, Then it is refused. (error case)

*Depends on:* BF-003

**Notes:** Was FF-181. The three sharing modes are the kernel's (TF-220, A8); this requirement keeps the default mode per master type and what a change of mode does to documents.

#### BF-005 Organisation-specific configuration
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Organisations

**Requirement:** Numbering sequences, document templates, approval configurations, tolerances, budget validation switches, receipt approval requirement and default match levels shall be settable per organisation, overriding tenant values for records owned by that organisation (TF-221); the tenant console shows effective values per organisation.

**Why:** Units run differently.

**Acceptance criteria:**

- AC1 Given a numbering sequence for organisation A with prefix A-PO, When an order of A is issued, Then its number starts with A-PO.
- AC2 Given no override for B, When an order of B is issued, Then the tenant sequence is used.
- AC3 Given a template missing for both A and the tenant, When rendering, Then it is refused naming the template. (error case)

*Depends on:* TF-221, BF-161

**Notes:** Was FF-183. The organisation scope of configuration is the kernel's (TF-221: settings in A6, numbering and templates in S4, approval configurations in S5); this requirement keeps which business configuration is set per organisation. Open point G-03 (section 12.2).

#### BF-006 Kinds of organisation unit
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* Organisations

**Requirement:** The business layer shall supply the kinds of the kernel's organisation units through `IOrganisationKinds` (TF-034): legal entity, business unit, department and team, with the kinds each may hold as children; a unit's kind decides which business attributes it carries, so a legal entity carries those of BF-001 and a department none of them.

**Why:** The unit tree is a kernel mechanism without business meaning (ADR-0029); this layer says what its units are.

**Acceptance criteria:**

- AC1 Given the business layer installed, When an administrator adds a unit, Then the kinds offered are those the parent's kind may hold.
- AC2 Given a department, When an administrator adds a legal entity under it, Then it is refused naming the kinds a department may hold. (error case)
- AC3 Given a unit of the kind legal entity, When it is opened, Then its finance attributes (BF-001) are shown, and a department shows none.

*Depends on:* TF-034, BF-001

**Notes:** `[Decision]`: which kind may hold which. Proposed: the root holds legal entities and business units; a legal entity holds business units and departments; a business unit holds business units and departments; a department holds teams; a team holds none. A purchasing group is not a kind (BF-007). Open point G-02 keeps its default: any unit may own documents, and the legal entity above it supplies the chart of accounts and the tax registration.

#### BF-007 Purchasing groups
*Priority:* **Should** · *Tag:* `[New]` `[Decision]` · *Area:* Organisations

**Requirement:** Purchasing groups shall be a business master, not a unit kind: a group of buyers (BF-021), the categories it covers (BF-042) and the units it serves; a group serves several units and owns no records.

**Why:** Buyers are organised by what they buy and for whom, across units. A unit kind would put a group at one place in the tree and make it an owner of records, and a purchasing group is neither.

**Acceptance criteria:**

- AC1 Given a purchasing group covering the category IT hardware and serving units A and B, When it is opened, Then its buyers, categories and units are listed.
- AC2 Given a purchasing group, When someone tries to make it the owning organisation of a record, Then it is refused, because a purchasing group is no unit. (error case)
- AC3 Given a request line of the category IT hardware in unit A, When a product asks which purchasing group serves it, Then the group is named.

*Depends on:* BF-021, BF-042, TF-034

**Notes:** Corrects the A1 amendment in `docs/prompts/S2.md`, which listed purchasing group among the unit kinds. `[Decision]`: what the groups are used for. Proposed: the products find the purchasing group of a line from its category and unit, for the buyers' work (the requests a group's buyers convert into orders) and as approvers or observers through a resolver (BF-143); a group gives no visibility and no permission of its own.

### 5.2 People (BF-021 to BF-040)

The people master linked to kernel principals: employee identifier, designation, job grade, the manager as the reporting line, business groups for routing and notifications (not for visibility, and not roles), and a person's defaults. It implements the kernel's `IReviewEscalation` (BF-026).

#### BF-021 People master
*Priority:* **Must** · *Tag:* `[New]` · *Area:* People

**Requirement:** The people master shall hold, for each person who works in the tenant, a link to their kernel principal (TF-010), an employee identifier unique in the tenant, a designation, a job grade, a manager, business groups, a default unit, a default ship-to location and a default account string; a person has one principal, and a principal at most one person.

**Why:** Approvals, notifications and defaults need business facts about people, such as who their manager is and which grade they hold, which the kernel's principals do not carry by design.

**Acceptance criteria:**

- AC1 Given a principal, When a person is created for it with an employee identifier, a designation, a job grade, a manager and a default unit, Then the person is listed with them.
- AC2 Given an employee identifier another person holds, When saved, Then it is refused naming the field. (error case)
- AC3 Given a principal linked to a person, When a second person is linked to it, Then it is refused. (error case)
- AC4 Given a person whose principal is disabled (TF-010), When people are offered as managers or approvers, Then that person is not offered.

*Depends on:* TF-010, TF-034, BF-022, BF-023, BF-024

**Notes:** Loaded by import and by ERP or HR sync like the other masters (BF-206, TF-206).

#### BF-022 Designations and job grades
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* People

**Requirement:** Designations and job grades shall be business lookups (BF-121) with a code and a name, the job grades in an order from lowest to highest, so that the delegation of authority matrix can name a grade and those above it (BF-142).

**Why:** The delegation of authority matrix decides by designation and job grade; both must be shared lists, not free text.

**Acceptance criteria:**

- AC1 Given grades G1 to G5 in order, When a matrix row names G3 and above, Then people of G3, G4 and G5 match it.
- AC2 Given a designation people hold, When its deletion is requested, Then it is refused, and it can be deactivated. (error case)
- AC3 Given a code another grade has, When saved, Then it is refused. (error case)

*Depends on:* BF-121, TF-201

**Notes:** `[Decision]`: an order among the grades, so a row can say "and above". Proposed as written; designations have no order.

#### BF-023 Manager and the reporting line
*Priority:* **Must** · *Tag:* `[New]` · *Area:* People

**Requirement:** Each person shall have at most one manager, another active person; the managers form the reporting line, which the kernel's workflow resolves as the manager chain (TF-091); a cycle in the reporting line is refused.

**Why:** Approval by manager chain (REQ-007) and escalation to the manager (BF-026) need the line.

**Acceptance criteria:**

- AC1 Given A reports to B and B to C, When a manager chain of two levels is resolved for A, Then it is B and C.
- AC2 Given B reports to A, When A is set to report to B, Then it is refused naming the cycle. (error case)
- AC3 Given a chain that ends before the levels asked, When it is resolved, Then the managers found are returned and the configuration's fallback applies to the rest (TF-091).

*Depends on:* BF-021, TF-091

#### BF-024 Business groups
*Priority:* **Should** · *Tag:* `[New]` · *Area:* People

**Requirement:** Business groups shall be teams of people for routing and notifications: an approval step, a task or a notification may name a group and goes to its members; a business group gives no visibility and is not a role.

**Why:** Work often goes to a team rather than to one person. Keeping groups apart from roles and visibility stops a routing list from becoming a way to see records.

**Acceptance criteria:**

- AC1 Given a business group of three members named on an approval step, When the step starts, Then each member who can read the record is an approver (TF-243).
- AC2 Given a member of a group, When they open a record their roles and units do not allow, Then it is refused, because group membership grants nothing. (error case)
- AC3 Given a group without active members named on a step, When the chain is built, Then the configuration's fallback applies (TF-091).

*Depends on:* BF-021, TF-090, TF-091, TF-150, TF-243

#### BF-025 A person's defaults on new documents
*Priority:* **Should** · *Tag:* `[New]` `[Decision]` · *Area:* People

**Requirement:** A person's default unit, default ship-to location and default account string shall be proposed on the documents they create, and stay editable.

**Why:** Requesters raise many documents for the same unit, delivery point and cost; proposing them saves keying and mistakes.

**Acceptance criteria:**

- AC1 Given a person with a default unit, ship-to location and account string, When they create a document, Then the three are proposed and can be changed.
- AC2 Given a default account string that has become inactive, When a document is created, Then it is not proposed and the person is told why. (error case)
- AC3 Given a default ship-to location of another legal entity than the document's, When the document is created, Then it is not proposed.

*Depends on:* BF-021, BF-062, BF-081, TF-034

**Notes:** `[Decision]`: the default unit beside the kernel's home unit (TF-034) and the session's working unit (A8). Proposed: the default unit is the home unit unless the person sets another, and a working unit chosen in the session wins.

#### BF-026 Escalation to the reviewer's manager
*Priority:* **Should** · *Tag:* `[New]` · *Area:* People

**Requirement:** The business layer shall implement the kernel's `IReviewEscalation` (A7): an overdue access review escalates to the reviewer's manager from the people master, and to the campaign's owner when the reviewer has no active manager.

**Why:** The kernel knows no managers and names the campaign's owner (TF-038 AC3, ADR-0037); the people master knows who the manager is.

**Acceptance criteria:**

- AC1 Given a review overdue by 7 days whose reviewer reports to M, When the daily pass runs, Then M is the escalation's target.
- AC2 Given a reviewer without an active manager, When the escalation runs, Then the campaign's owner is the target, as without this layer. (error case)

*Depends on:* BF-023, TF-038

### 5.3 Items and classification (BF-041 to BF-060)

Items, categories and commodities (with UNSPSC from BF-205), units of measure with conversions. Supplier items are the Catalogue product's.

#### BF-041 Items
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Items and classification

**Requirement:** Items (code auto or manual, name, description, type product or service, unit, category, commodity, tax classification, default price and currency, attributes, images, status) shall be maintained with translations for name and description.

**Why:** Standard items for requests and catalogues.

**Acceptance criteria:**

- AC1 Given a new item with auto code, When saved, Then it receives the next code.
- AC2 Given an inactive item, When a requester searches, Then it is not offered.
- AC3 Given a duplicate manual code, When saved, Then it is refused. (error case)

*Depends on:* BF-042, BF-043, TF-143

**Notes:** Was FF-040.

#### BF-042 Categories and commodities
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Items and classification

**Requirement:** A category tree with management groups and a commodity hierarchy mapped to categories shall classify items, request lines, suppliers and contracts and shall be usable in approval conditions and reports.

**Why:** Classification for routing and analytics.

**Acceptance criteria:**

- AC1 Given a category with a management group, When a request line of that category is submitted, Then the group can be resolved as approvers.
- AC2 Given a duplicate category name under the same parent, When saved, Then it is refused. (error case)
- AC3 Given a commodity moved, When viewed, Then its items keep the mapping.

**Notes:** Was FF-010.

#### BF-043 Units of measure
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Items and classification

**Requirement:** Units with code, name, type and conversions (for example 1 box = 12 each) shall be maintained; conversions apply when receipts or invoices use a different unit than the order line.

**Why:** Real deliveries use other units.

**Acceptance criteria:**

- AC1 Given an order in boxes and a receipt in each, When received, Then quantity is converted and compared in the order unit.
- AC2 Given a unit without conversion to the order unit, When used on a receipt, Then it is refused. (error case)
- AC3 Given a duplicate unit code, When saved, Then it is refused.

**Notes:** Was FF-013.

#### BF-044 Item imports
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Items and classification

**Requirement:** Items shall be importable through the kernel import framework with templates and validation.

**Why:** Bulk loading at go-live.

**Acceptance criteria:**

- AC1 Given a valid item file, When imported, Then items are created and codes allocated.
- AC2 Given rows with unknown units, When imported, Then they are reported and skipped. (error case)
- AC3 Given a dry run, When finished, Then nothing is created and the report shows counts.

*Depends on:* BF-041, TF-205

**Notes:** Was FF-049, the items; supplier items are CAT-006. Placed by the test: the import of the item master belongs with it.

### 5.4 Finance (BF-061 to BF-080)

Chart of accounts and segments, account strings, budgets, fiscal calendar, currencies and exchange rates, tax codes and classifications, payment and shipping terms. GL codes are one of the chart's segments, not a master of their own (BF-061). HSN and SAC codes (BF-074) and projects as cost objects (BF-075) are new in this document; the nightly reconciliation of budgets is BF-073.

#### BF-061 Chart of accounts and segments
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Finance administrators shall define charts of account per legal entity with ordered segments (name, length, data type, allowed values or value ranges, mandatory), buying group, billing address, and default match levels for products and services.

**Why:** Account strings are built from segments.

**Acceptance criteria:**

- AC1 Given a chart with three segments, When an account string is entered, Then each part is validated against its segment values.
- AC2 Given a chart of another legal entity, When selected on a document, Then it is refused. (error case)
- AC3 Given segment values changed, When existing documents are viewed, Then their strings are unchanged.

*Depends on:* BF-001

**Notes:** Was FF-002. GL codes are one of the chart's segments, not a master of their own: the GL code of a line is the value of its GL segment in the account string.

#### BF-062 Account strings
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Finance

**Requirement:** Valid account strings shall be stored as master records (string, segment values, owner, active) so that budgets, lists and reports reference them; invalid combinations shall be refused and inactive strings shall not be selectable on new lines.

**Why:** Avoid free-text coding mistakes.

**Acceptance criteria:**

- AC1 Given an inactive string, When a line is coded with it, Then it is refused with the status shown. (error case)
- AC2 Given a new combination allowed by segment rules, When first used, Then it is created as a master record (if the setting allows) or refused (if the setting requires pre-defined strings).
- AC3 Given a string owner, When a budget line owner is not set, Then the string owner is used as the dynamic approver.

*Depends on:* BF-061

**Notes:** Was FF-003.

#### BF-063 Budget periods
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Finance administrators shall create budget periods (name, start, end, owner, status Open or Closed) per legal entity; closed periods accept no new reservations.

**Why:** Budget control by period.

**Acceptance criteria:**

- AC1 Given an Open period, When a line is split to it, Then it is accepted.
- AC2 Given a Closed period, When a new request uses it, Then it is refused. (error case)
- AC3 Given overlapping periods for one legal entity, When saved, Then a warning is shown (overlaps allowed for quarters inside years).

*Depends on:* BF-001

**Notes:** Was FF-004.

#### BF-064 Budget lines
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Budget lines shall be unique per budget period and account string, with owner, amount, reserved, consumed and available, and a consumption history with document references.

**Why:** Where the money is.

**Acceptance criteria:**

- AC1 Given a duplicate period and string, When saved, Then it is refused. (error case)
- AC2 Given amount 100,000 with reserved 20,000 and consumed 30,000, When viewed, Then available shows 50,000.
- AC3 Given history requested, When viewed, Then each row shows document type, number, amount and event (reserved, confirmed, released).

*Depends on:* BF-063, BF-062

**Notes:** Was FF-005.

#### BF-065 Budget availability check with reservation
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Finance

**Requirement:** When a request, non-order invoice or other budget-relevant document is submitted and budget validation is on for its type, the system shall lock the budget lines in a fixed order, compute required per split, compare with available, block or warn per configuration, and reserve the required amounts in the same transaction.

**Why:** Gap: race conditions in the current product.

**Acceptance criteria:**

- AC1 Given two requests submitted at once against the last 1,000 available, When processed, Then one succeeds and the other is refused (blocking) or warned (warning mode).
- AC2 Given no budget line for a string with validation on, When submitted, Then "No budget line exists for account string X" is shown. (error case)
- AC3 Given warning mode, When exceeded, Then submission proceeds, the shortfall is stored on the document and shown to approvers.

*Depends on:* BF-064, TF-084

**Notes:** Was FF-006.

#### BF-066 Budget consumption and release
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Finance

**Requirement:** Reservations shall become consumption at approval (or at submission when configured), be released on rejection, cancellation or close-short, be transferred from request to order to invoice without double counting, and be reversed by credit notes.

**Why:** Correct available budget at all times.

**Acceptance criteria:**

- AC1 Given an approved request later converted to an order and invoiced, When viewed, Then the budget line shows one consumption, not three.
- AC2 Given a request cancelled, When processed, Then its reservation is released.
- AC3 Given a credit note approved, When processed, Then consumption decreases by its amount. (edge)
- AC4 Given a release attempted for a reservation that no longer exists, When processed, Then it is refused and reported as an integrity error. (error case)

*Depends on:* BF-065

**Notes:** Was FF-007.

#### BF-067 Budget adjustments and transfers
*Priority:* **Should** · *Tag:* `[Improved]` · *Area:* Finance

**Requirement:** Permitted users shall adjust a budget line (positive or negative, reason mandatory) or transfer between lines in one action, never below consumed plus reserved, with approval when configured.

**Why:** Budgets change during the year.

**Acceptance criteria:**

- AC1 Given an adjustment that would make available negative, When saved, Then it is refused. (error case)
- AC2 Given a transfer of 10,000 from A to B, When approved, Then A decreases and B increases by 10,000 with one audit trail.
- AC3 Given adjustments, When listed, Then actor, reason and time are shown.

*Depends on:* BF-064, TF-090

**Notes:** Was FF-008.

#### BF-068 Budget reports
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Budget consumption by line, period and document type with reserved, consumed and available, and drill-down to documents.

**Why:** Finance visibility.

**Acceptance criteria:**

- AC1 Given consumption, When the report runs, Then totals match budget lines.
- AC2 Given drill-down, When clicked, Then documents are listed.
- AC3 Given export, When run, Then a file is produced. (edge)
- AC4 Given a user without the budget report permission, When run, Then it is refused. (error case)

*Depends on:* BF-064

**Notes:** Was FF-162.

#### BF-069 Fiscal calendar
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Fiscal years with periods (no overlaps, no gaps) per legal entity, with open and closed states; documents carry the fiscal period of their date; closed periods refuse new documents dated inside them unless the user has the permission to post into closed periods.

**Why:** Period reporting.

**Acceptance criteria:**

- AC1 Given periods with a gap, When saved, Then it is refused. (error case)
- AC2 Given a document dated in a closed period, When created by a user without the permission, Then it is refused.
- AC3 Given a document, When viewed, Then its fiscal period is shown.

*Depends on:* BF-001

**Notes:** Was FF-009.

#### BF-070 Currencies and exchange rates with revisions
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Currencies (code, decimals, symbol) and exchange rates (from, to, chart of account or legal entity, validity range, rate) shall be maintained through a revision workflow (draft, submitted, approved, applied), with a rate feed connector as a later option; conversions use the rate valid on the document date.

**Why:** Correct conversion with control.

**Acceptance criteria:**

- AC1 Given a revision approved, When applied, Then rates are valid from the start date.
- AC2 Given overlapping validity for the same pair and scope, When saved, Then it is refused. (error case)
- AC3 Given no valid rate, When a line is converted, Then the save is refused with the pair and date named.

*Depends on:* TF-090

**Notes:** Was FF-011.

#### BF-071 Tax codes and classifications
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Finance

**Requirement:** Tax codes with components and rates, validity and country, tax classifications for items and services (such as harmonised codes), and a place-of-supply rule setting shall drive tax lines on orders and invoices; totals round per currency decimals using half away from zero; country-specific rule sets (Indian GST with CGST, SGST and IGST by place of supply) are delivered as accelerator packs (BF-204).

**Why:** Correct invoice totals; Indian GST readiness.

**Acceptance criteria:**

- AC1 Given a line of 1,000 with an 18 percent code of two components, When computed, Then two tax lines of 90 each are created.
- AC2 Given an expired tax code, When used on a new line, Then it is refused. (error case)
- AC3 Given rounding, When totals are computed, Then line taxes round to two decimals and the header equals the sum of lines.

**Notes:** Was FF-012. Decision F-08 confirmed.

#### BF-072 Payment and shipping terms
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Finance

**Requirement:** Payment terms (net days, discount percent and days) and shipping terms shall be maintained and defaulted from supplier to order to invoice; invoice due dates and discount dates shall be computed from terms.

**Why:** Standard commercial terms.

**Acceptance criteria:**

- AC1 Given terms net 30 and an invoice date, When saved, Then the due date is 30 days later.
- AC2 Given a discount of 2 percent within 10 days, When viewed, Then the discount date and amount are shown.
- AC3 Given a term deleted while in use, When attempted, Then it is refused. (error case)

**Notes:** Was FF-014.

#### BF-073 Nightly reconciliation of budgets
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Finance

**Requirement:** A nightly job shall recompute budget reserved and consumed from the consumption history, whose rows name their documents (BF-064), compare them with the stored values, write differences to a drift report and alert operators; drift is an incident, not auto-healed. It runs in the one nightly reconciliation window of the kernel's jobs (TF-120 to TF-124).

**Why:** Counters are a cache; documents are the truth.

**Acceptance criteria:**

- AC1 Given consistent data, When the job runs, Then zero drift rows exist and a success record is stored.
- AC2 Given a manual database change that breaks a counter, When the job runs, Then the drift row names the line and values and an alert fires. (error case)
- AC3 Given the job fails, When detected, Then an alert fires.

*Depends on:* BF-064, BF-066, TF-120, TF-123

**Notes:** Was FF-133, the budgets. Business recomputes from its own consumption history and needs nothing from the products (ADR-0042). Orders, Receiving and Invoicing reconcile their own counters in ORD-121, RCV-083 and INV-027, in the same window.

#### BF-074 HSN and SAC codes
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* Finance

**Requirement:** HSN codes for goods and SAC codes for services shall be explicit tax classifications (BF-071): an item, or a line without an item, may carry one, a product an HSN code and a service a SAC code; the Indian GST pack (BF-204) supplies the code lists and whether a code is required.

**Why:** Indian GST invoices and e-invoices carry the HSN or SAC code of every line; naming them, rather than leaving them among "harmonised codes", makes them testable.

**Acceptance criteria:**

- AC1 Given an item of type product with the HSN code 8471, When it is used on a line, Then the line carries 8471 as its classification.
- AC2 Given a service item, When an HSN code is set on it, Then it is refused, since a service carries a SAC code. (error case)
- AC3 Given the Indian GST pack with codes required, When a line without a code is submitted, Then it is refused naming the line. (error case)
- AC4 Given a code that is not in the pack's list, When saved on an item, Then it is refused naming the code. (error case)

*Depends on:* BF-041, BF-071, BF-204

**Notes:** `[Decision]`: where a code is required. Proposed: on the lines of documents of a legal entity with a GST registration, as the pack sets it.

#### BF-075 Projects as cost objects
*Priority:* **Should** · *Tag:* `[New]` `[Decision]` · *Area:* Finance

**Requirement:** Projects shall be a master of cost objects, each with a code, a name, validity dates, an owning unit and a link to budget lines (BF-064); a line may be coded to an active project within its validity, and a project outside its validity or inactive is not offered on new lines.

**Why:** Spend is followed by project as well as by account string. A project here is a cost object; projects and tasks as a product are later scope (`products/projects-and-tasks-scope.md`).

**Acceptance criteria:**

- AC1 Given an active project valid this year, When a line is coded, Then the project is offered and stored on the line.
- AC2 Given a project whose validity has ended, When it is used on a new line, Then it is refused naming the project. (error case)
- AC3 Given a code another project has, When saved, Then it is refused. (error case)
- AC4 Given a project linked to two budget lines, When its consumption is reported, Then the consumption of both lines is shown.

*Depends on:* BF-064, TF-034, TF-220

**Notes:** `[Decision]`: what the budget link does. Proposed: it names the budget lines the project may consume and reports them together; the budget check of a line stays by its account string (BF-065).

### 5.5 Locations (BF-081 to BF-100)

Locations of three types, ship-to, bill-to and warehouse (BF-083), each linked to its legal entity, which requests, orders, receipts and invoices offer.

#### BF-081 Ship-to locations
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Locations

**Requirement:** Ship-to locations per legal entity or unit with addresses shall be offered on lines and carried to orders.

**Why:** Correct delivery.

**Acceptance criteria:**

- AC1 Given locations for the unit, When a line is edited, Then they are offered.
- AC2 Given a location of another legal entity, When saved on the line, Then it is refused. (error case)
- AC3 Given a location on the line, When the order is created, Then the order line carries it.

*Depends on:* BF-001

**Notes:** Was FF-062.

#### BF-082 Warehouses and locations
*Priority:* **Should** · *Tag:* `[Improved]` · *Area:* Locations

**Requirement:** Warehouses and locations per legal entity (code, name, address, active) shall be maintained in a screen and offered on receipts.

**Why:** Gap: no screen in the current product.

**Acceptance criteria:**

- AC1 Given a warehouse created, When a receipt is made, Then it is offered.
- AC2 Given a duplicate code, When saved, Then it is refused. (error case)
- AC3 Given a warehouse deactivated, When used on a new receipt, Then it is not offered.

*Depends on:* BF-001

**Notes:** Was FF-100.

#### BF-083 Location types and bill-to locations
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Locations

**Requirement:** Every location shall have a type, ship-to, bill-to or warehouse, and be linked to its legal entity; each legal entity has one default bill-to location, and more are allowed.

**Why:** A legal entity is billed at an address of its own, sometimes at more than one, and the locations master already holds the ship-to locations and the warehouses.

**Acceptance criteria:**

- AC1 Given a legal entity with two bill-to locations, one of them its default, When a billing address is chosen for a document of that legal entity, Then the default is proposed and the other is offered.
- AC2 Given a bill-to location without a legal entity, When saved, Then it is refused. (error case)
- AC3 Given a second bill-to location marked default for the same legal entity, When saved, Then the first loses the default.
- AC4 Given a warehouse, When billing addresses are offered, Then it is not among them.

*Depends on:* BF-001, BF-081, BF-082

**Notes:** Decided at the PRD split's checkpoint (section 12.1). The billing address among a legal entity's attributes (BF-001) is its default bill-to location.

### 5.6 Supplier master (BF-101 to BF-120)

The supplier record and its parts, which every product reads, and its enablement per organisation. The life cycle is the Supplier management product's.

#### BF-101 Supplier master
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Supplier master

**Requirement:** The supplier master shall hold identity (number auto or manual, legal name, trade name, type, registration type, country), tax identifiers (validated per country rules), classification (categories, commodities, preferred flag, diversity attributes), commercial defaults (currency, payment terms, shipping terms, match levels), portal settings (allowed channels), status fields and custom fields.

**Why:** Central master of the products.

**Acceptance criteria:**

- AC1 Given a supplier with unique identifiers, When saved, Then it is visible to buyers with status.
- AC2 Given a tax identifier already registered, When saved, Then it is refused naming the field. (error case)
- AC3 Given India validation on, When a GST or PAN with a wrong format is entered, Then it is refused.

*Depends on:* BF-042, BF-072

**Notes:** Was FF-022.

#### BF-102 Supplier addresses
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier master

**Requirement:** Addresses (type, lines, city, state, country, postal code, one primary, one remit-to) maintained by supplier or buyer, used on orders and invoices.

**Why:** Shipping and remittance.

**Acceptance criteria:**

- AC1 Given a second address marked primary, When saved, Then the first loses the primary flag.
- AC2 Given an address used on an open order, When deleted, Then it is refused; it can be deactivated. (error case)
- AC3 Given a remit-to address, When an invoice is created, Then it defaults to it.

*Depends on:* BF-101

**Notes:** Was FF-026.

#### BF-103 Supplier contacts
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Supplier master

**Requirement:** Contacts (name, email unique, phone, role) shall be maintained; a contact with portal access is a Supplier Network user of the supplier organisation and is visible to the tenant as a linked contact; the tenant never holds supplier credentials.

**Why:** Portal access control.

**Acceptance criteria:**

- AC1 Given a network user of the supplier organisation, When the supplier is opened in the tenant, Then the user is listed as a linked contact with name, email, phone and role.
- AC2 Given a buyer tries to set a password for a supplier contact, When saved, Then it is refused because the tenant never holds supplier credentials. (error case)

*Depends on:* BF-101, TF-230

**Notes:** Was FF-027, the contact data; the portal users are SPX-005. AC1 and AC2 are written for the split from the requirement's own clauses.

#### BF-104 Supplier sites
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier master

**Requirement:** Sites (name, address, tax registration, contacts, bank link, status) with site-level approval when configured; orders may target a site; invoices carry the site's tax registration.

**Why:** Multi-site suppliers.

**Acceptance criteria:**

- AC1 Given a site with a tax registration, When an invoice for that site is created, Then the registration is used.
- AC2 Given a site without mandatory documents when required, When published, Then it is refused. (error case)
- AC3 Given a site deactivated, When a new order is created, Then it is not offered.

*Depends on:* BF-101

**Notes:** Was FF-028.

#### BF-105 Supplier bank accounts
*Priority:* **Must** · *Tag:* `[Improved]` · *Area:* Supplier master

**Requirement:** Bank accounts (bank, branch, account number encrypted, codes, currency, primary, attachments) shall be maintained; unverified accounts shall not be used for payment instructions.

**Why:** Bank account change fraud is the main payables risk.

**Acceptance criteria:**

- AC1 Given a bank account saved, When the database is read without the application, Then the account number is unreadable.
- AC2 Given a bank account whose verification is not complete, When it is chosen for a payment instruction, Then it is refused. (error case)

*Depends on:* BF-101, TF-074

**Notes:** Was FF-029, the account data and the rule that an unverified account is not used for payment; the verification is SUP-023. AC1 and AC2 are written for the split from the requirement's own clauses. Decision F-05 confirmed.

#### BF-106 Supplier configuration defaults
*Priority:* **Should** · *Tag:* `[Carried]` · *Area:* Supplier master

**Requirement:** Tenant administrators shall set defaults for new suppliers (payment terms, currency, match levels, required sections, required documents, portal channels) applied at creation and editable per supplier.

**Why:** Consistency and speed.

**Acceptance criteria:**

- AC1 Given defaults set, When a supplier is created, Then fields are pre-filled.
- AC2 Given a default changed, When existing suppliers are viewed, Then their values are unchanged.
- AC3 Given a user without the configuration permission, When saving, Then it is refused. (error case)

*Depends on:* BF-101, TF-057

**Notes:** Was FF-035.

#### BF-107 Supplier categories and preferred status
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Supplier master

**Requirement:** Suppliers shall be classified by categories and commodities they serve, with preferred or approved status per category; request and order supplier suggestions shall use these classifications.

**Why:** Guide buyers to the right suppliers.

**Acceptance criteria:**

- AC1 Given a line of category X, When suppliers are suggested, Then preferred suppliers for X appear first.
- AC2 Given a supplier removed from a category, When suggested, Then it no longer appears for that category.
- AC3 Given a supplier on hold, When suggested, Then it is excluded. (edge)
- AC4 Given a category that does not exist, When assigned, Then it is refused. (error case)

*Depends on:* BF-101, BF-042

**Notes:** Was FF-038.

#### BF-108 Supplier enablement per organisation
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Supplier master

**Requirement:** A supplier shall register once per tenant and be enabled per organisation with organisation-specific terms (payment and shipping terms, currency, match levels, preferred flag, status On or On Hold); documents of an organisation may use only suppliers enabled in it.

**Why:** One registration for the supplier, separate control for each unit.

**Acceptance criteria:**

- AC1 Given a supplier enabled in A but not B, When a buyer of B creates an order for it, Then it is refused with an enablement action offered. (error case)
- AC2 Given organisation terms set, When an order in that organisation is created, Then the organisation's terms default.

*Depends on:* BF-101, BF-003

**Notes:** Was FF-182, the enablement and its terms. Its optional organisation approval is SUP-006, since Business never depends on a product (ADR-0042); its clause that supplier users see the documents of every organisation that enabled them, with its AC3, is SPX-004's, which says the same. Decision G-01 default.

### 5.7 Lookups (BF-121 to BF-140)

The business lists every product uses, built on the kernel's custom lookups (TF-201): which lists exist, who maintains them (BF-121), the seeded values (BF-122).

#### BF-121 Business lookups
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Lookups

**Requirement:** The business layer shall declare the business lists on the kernel's custom lookups (TF-201), each naming the business module that owns it, its values maintained by tenant administrators who hold that module's configuration permission: supplier types, registration types and diversity attributes (BF-101); address types (BF-102); contact roles (BF-103); designations and job grades (BF-022); the reasons for holds, cancellations, rejections, returns, disputes and close-short, which the products share; and charge types.

**Why:** Products need the same lists for the same things; one owner per list keeps them alike and stops each product from growing its own.

**Acceptance criteria:**

- AC1 Given a new tenant, When the lookups are opened, Then every list of this requirement exists with its owning module named.
- AC2 Given a user without the owning module's configuration permission, When they change a value, Then it is refused. (error case)
- AC3 Given a value in use (BF-207), When its deletion is requested, Then it is refused, and it can be deactivated. (error case)

*Depends on:* TF-201, BF-207

**Notes:** A list one product alone uses, such as Supplier management's risk flag types (SUP-043), belongs to that product.

#### BF-122 Seeded values of the business lookups
*Priority:* **Should** · *Tag:* `[New]` `[Decision]` · *Area:* Lookups

**Requirement:** The business lookups shall be seeded with starter values that tenant administrators may edit, deactivate or extend; a seed never overwrites a tenant's values (TF-088).

**Why:** A tenant can raise its first request within a day (goal 3) only if the common lists are not empty.

**Acceptance criteria:**

- AC1 Given a new tenant, When it is provisioned, Then the charge types freight, handling and other exist.
- AC2 Given a tenant that renamed a seeded value, When a new seed version runs, Then the tenant's name is kept.
- AC3 Given a seed step that fails, When the upgrade runs, Then it stops for that tenant and reports. (error case)

*Depends on:* BF-121, TF-088

**Notes:** `[Decision]`: the starter values. Proposed: those the archived PRD names, the charge types freight, handling and other, and remit-to among the address types; the other lists start empty for the tenant to fill.

### 5.8 Approvals and delegation of authority (BF-141 to BF-160)

Approval configurations per document type on the kernel's workflow engine (TF-090 to TF-099, S5), and the delegation of authority matrix: approval limits by amount and currency, by designation, job grade or role, by category, per legal entity or unit, with validity dates and audit. Delegation of duties (TF-037, built in A7) is the kernel's; this matrix decides who may approve what.

#### BF-141 Approval configurations per document type
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Approvals and delegation of authority

**Requirement:** The business layer shall hold the approval configurations of every document type on the kernel's workflow engine (TF-090 to TF-099): the document types and kinds (new and change) the products register, the conditions on their registered fields, the stages and approvers, per tenant and per organisation (BF-005), starting from the starter set (BF-201).

**Why:** Approval rules are business configuration of a kernel mechanism (ADR-0029); one place for them keeps every product's approvals alike and auditable.

**Acceptance criteria:**

- AC1 Given a product that registered the document types order new and order change, When an administrator opens the approval configurations, Then both are offered with their registered fields.
- AC2 Given a configuration for organisation A and one for the tenant, When a document of A is submitted, Then A's applies.
- AC3 Given a condition on a field the document type does not register, When saved, Then it is refused naming the field. (error case)

*Depends on:* TF-090, TF-095, TF-221, BF-005, BF-201

**Notes:** Built with the kernel's workflow engine (S5).

#### BF-142 Delegation of authority matrix
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* Approvals and delegation of authority

**Requirement:** Tenant administrators shall maintain a delegation of authority matrix: rows of approval limits by amount and currency, for a designation, a job grade or a role, for a category or all, per legal entity or unit, each with validity dates; every change is audited with before and after (TF-060). The matrix decides who may approve what; the kernel's delegation of duties (TF-037) decides who acts for whom.

**Why:** Who may approve up to what amount is a business control auditors ask to see (goal 2).

**Acceptance criteria:**

- AC1 Given a row "grade G3, category IT, legal entity L1, up to 50,000 INR, valid this year", When the matrix of L1 is listed, Then the row is shown with its validity.
- AC2 Given a row whose end date is before its start date, When saved, Then it is refused. (error case)
- AC3 Given a row changed, When the audit is searched, Then actor, time, before and after are recorded.
- AC4 Given a row past its validity, When approvers are resolved (BF-143), Then it is not used.

*Depends on:* BF-001, BF-022, BF-042, TF-060

**Notes:** `[Decision]`: two rows that match the same person and document. Proposed: the most specific row wins (a unit before its legal entity, a category before all), and between equals the lower limit. `[Decision]`: a row in another currency than the document. Proposed: the document's amount is converted with the rate valid on its date (BF-070).

#### BF-143 Approvers from the matrix
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Approvals and delegation of authority

**Requirement:** The matrix shall be a dynamic approver resolver of the kernel's workflow (TF-091, its authority matrix by amount band and dimensions): for a document's amount, category and unit it names the people whose row allows it; a person who cannot read the document is left out (TF-243); when nobody is named, the configuration's fallback applies.

**Why:** A configuration says "approved within the delegation of authority" once instead of listing approvers per amount band.

**Acceptance criteria:**

- AC1 Given a request of 40,000 INR in category IT of legal entity L1 and a row allowing grade G3 up to 50,000, When the stage resolves the matrix, Then the G3 people of L1 who can read the request are its approvers.
- AC2 Given a document above every row's limit, When the matrix is resolved, Then nobody is named and the configuration's fallback applies. (error case)

*Depends on:* BF-021, BF-142, TF-091, TF-243

### 5.9 Numbering, templates and notifications (BF-161 to BF-180)

Document type settings and document templates; number series per document type and unit on the kernel's numbering service (TF-166, BF-163); and business notification templates and who is told what, on the kernel's notifications (TF-150 to TF-156, BF-164).

#### BF-161 Document type settings
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Numbering, templates and notifications

**Requirement:** Settings per document type for budget validation and blocking, consumption trigger, multi-currency lines, approval configurations, numbering, templates, mandatory custom fields and acknowledgement reminder days.

**Why:** Configurable control.

**Acceptance criteria:**

- AC1 Given a setting changed, When the next document is created, Then it applies.
- AC2 Given an invalid value, When saved, Then it is refused. (error case)
- AC3 Given a change, When audited, Then before and after are recorded.

*Depends on:* TF-057

**Notes:** Was FF-170.

#### BF-162 Document templates for the products
*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* Numbering, templates and notifications

**Requirement:** Templates for order, change order, return note, receipt note and invoice acknowledgement per legal entity and language through kernel templates, with product placeholders documented.

**Why:** Branded documents.

**Acceptance criteria:**

- AC1 Given a template per legal entity, When rendered, Then it is used.
- AC2 Given a missing placeholder, When saved, Then it is refused. (error case)
- AC3 Given a preview, When requested, Then sample data renders.

*Depends on:* TF-162, TF-163

**Notes:** Was FF-173.

#### BF-163 Number series
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Numbering, templates and notifications

**Requirement:** Number series shall be configured per document type and unit on the kernel's numbering service (TF-166): prefix, suffix, length, reset rule, and the legal entity's numbering initials as a dynamic prefix (BF-001); a unit without a series of its own uses the tenant's (BF-005).

**Why:** Each legal entity and business unit numbers its documents its own way, on one atomic service.

**Acceptance criteria:**

- AC1 Given a series for orders of organisation A with the prefix A-PO, When an order of A is numbered, Then its number starts with A-PO.
- AC2 Given the dynamic prefix on and the legal entity's initials L1, When a document of L1 is numbered, Then L1 appears in the number.
- AC3 Given a document type with no series for its unit or the tenant, When a document is submitted, Then it is refused with a configuration message naming the type. (error case)

*Depends on:* TF-166, BF-001, BF-005

#### BF-164 Business notification templates and audiences
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* Numbering, templates and notifications

**Requirement:** The business layer shall hold the templates of the products' notification keys per language, and per legal entity or unit where the tenant wants (TF-150, TF-221), and say who is told what: each key names its audience, a role on the document (requester, buyer owner, approver, supplier primary contact), a business group (BF-024) or a person; no notification goes to someone who cannot read its record (TF-243).

**Why:** The products declare what happens; the tenant decides, in its own words, who hears of it.

**Acceptance criteria:**

- AC1 Given the key "order issued" with the audience requester, When an order is issued, Then the requester of each of its lines is told, in their language's template.
- AC2 Given a template for legal entity L1 and a tenant template, When a document of L1 is notified, Then L1's template is used.
- AC3 Given a template with a placeholder its key does not offer, When saved, Then it is refused naming the placeholder. (error case)

*Depends on:* BF-024, TF-150, TF-151, TF-221, TF-243

**Notes:** `[Decision]`: the starter templates. Proposed: one per key, in English, active from provisioning, which the tenant edits.

### 5.10 Content visibility on masters (BF-181 to BF-200)

Which masters carry visibility tags, on the kernel mechanism TF-241: suppliers, items and projects here, contracts in the Contracts product.

#### BF-181 Masters that carry visibility tags
*Priority:* **Must** · *Tag:* `[New]` `[Decision]` · *Area:* Content visibility on masters

**Requirement:** Suppliers, items and projects shall carry visibility tags (TF-241): their tags are set by people who hold them (rule 5), a master without tags is visible to everyone who passes roles and units (rule 2), and the products' transactions take tags from them as each product says (rule 6). Contracts carry tags too, in the Contracts product.

**Why:** A buyer group that shares a unit but not its suppliers, items or projects needs them narrowed from the first record.

**Acceptance criteria:**

- AC1 Given a supplier tagged APAC and a buyer holding only LATAM, When the buyer lists suppliers, Then it is not shown.
- AC2 Given a buyer holding only APAC, When they tag an item LATAM, Then it is refused naming the tag. (error case)
- AC3 Given an item without tags, When anyone who passes roles and units searches items, Then it is shown.

*Depends on:* BF-041, BF-075, BF-101, TF-241

**Notes:** `[Decision]`: the tags of a new master. Proposed: a new master starts without tags, and its creator may add the tags they hold.

### 5.11 Setup and data (BF-201 to BF-220)

The approval starter set, go-live and organisation readiness as contributions to the kernel's `IUnitReadiness`, country accelerator packs, reference data from standards, and masters import, export and ERP sync.

#### BF-201 Approval configuration starter set
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** A starter set of approval configurations per document type (manager chain by amount bands, budget owner for requests, finance for invoices above a threshold, procurement head for suppliers) shall be seeded as inactive examples that administrators can activate and edit.

**Why:** Faster go-live.

**Acceptance criteria:**

- AC1 Given a new tenant, When the console opens, Then the starter configurations exist inactive.
- AC2 Given activation, When a document is submitted, Then the configuration applies.
- AC3 Given an edit, When saved, Then it is versioned by the kernel workflow. (edge)
- AC4 Given a starter configuration edited to reference an unknown field, When saved, Then it is refused. (error case)

*Depends on:* TF-090, TF-095

**Notes:** Was FF-174.

#### BF-202 Go-live checklist and data readiness
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** A tenant readiness view shall show what is missing before go-live: legal entities, chart of accounts, account strings, budgets, currencies, units, taxes, templates, numbering, approval configurations, suppliers, items, users and roles, with links to fix each.

**Why:** Goal 3 (first request within one day, section 1.2).

**Acceptance criteria:**

- AC1 Given missing templates, When the view loads, Then it is listed with a link.
- AC2 Given all items complete, When the view loads, Then it shows ready.
- AC3 Given a user without administration permission, When opening, Then it is refused. (error case)

*Depends on:* BF-161, TF-223

**Notes:** Was FF-175. Its checklist is made of contributions to the kernel's IUnitReadiness (A8). Its dependency on the supplier onboarding configuration (SUP-005) is dropped: Business never depends on a product, and a product that needs readiness items contributes its own to IUnitReadiness (TF-223, ADR-0042).

#### BF-203 Organisation readiness checklist
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** Adding an organisation shall open a readiness checklist (legal entity link, chart of accounts, account strings, budgets, numbering, templates, approval configurations, enabled suppliers, users and roles).

**Why:** Companies reorganise.

**Acceptance criteria:**

- AC1 Given a new organisation, When created, Then the checklist lists missing items with links.
- AC2 Given a user without administration permission, When they open an organisation's readiness checklist, Then it is refused. (error case)

*Depends on:* TF-223, BF-202

**Notes:** Was FF-188, the checklist's items, which this layer contributes to the kernel's IUnitReadiness; creating, deactivating and merging organisations are the kernel's TF-223 (A8). AC2 is written for the part, as BF-202 AC3 says it for the tenant.

#### BF-204 Country accelerator packs
*Priority:* **Should** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** Country-specific rules shall be delivered as accelerator packs installed per tenant by operators and configured by tenant administrators: reference data (tax codes and components, classifications, document types), validation rules (identifier formats, e-invoice fields), computation rules (for example Indian GST: CGST and SGST for intra-state, IGST for inter-state by place of supply, reverse charge flags) and report formats; packs are versioned and upgradable without code changes to the products.

**Why:** Decision F-08: Indian GST as an accelerator pack; other countries later.

**Acceptance criteria:**

- AC1 Given the Indian GST pack installed, When an intra-state invoice line is taxed, Then CGST and SGST components are computed; inter-state produces IGST.
- AC2 Given no pack installed, When a line is taxed, Then plain tax codes apply without country rules.
- AC3 Given a pack upgraded, When installed, Then existing documents keep their computed values and new documents use the new rules.
- AC4 Given a pack installation by a tenant administrator without operator rights, When attempted, Then it is refused. (error case)

*Depends on:* BF-071, TF-088

**Notes:** Was FF-176. Its dependency on e-invoicing readiness is reversed: INV-010 applies the packs' e-invoice field rules and depends on this requirement (ADR-0042). Decision G-05 default: operators install, tenant administrators configure.

#### BF-205 Reference data from standards
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** Categories shall support UNSPSC codes (with tenant-specific trees mapped to UNSPSC), items and locations shall support GS1 identifiers (GTIN, GLN), currencies ISO 4217, dates ISO 8601, locations UN/LOCODE and shipping terms Incoterms 2020, each as optional reference attributes that packs and integrations can rely on.

**Why:** Common language with ERPs, suppliers and networks.

**Acceptance criteria:**

- AC1 Given a category mapped to a UNSPSC code, When exported, Then the code is included.
- AC2 Given an item with a GTIN of wrong length or check digit, When saved, Then it is refused. (error case)

*Depends on:* BF-042, BF-041

**Notes:** Was FF-202, the reference data of the masters; the payment status codes (ISO 20022) with AC3 are INV-045, since Business never depends on a product (ADR-0042).

#### BF-206 Masters import, export and ERP sync
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** All finance masters shall be importable and exportable through the kernel import framework and API, with ERP as the optional system of record (inbound sync by schedule) and conflict reporting when a record changed in both systems.

**Why:** Every tenant loads masters from an ERP.

**Acceptance criteria:**

- AC1 Given a chart of accounts file, When imported, Then segments and strings are created and errors reported per row.
- AC2 Given a record changed in both systems, When synced, Then it is reported as a conflict and not overwritten. (error case)
- AC3 Given an export, When run, Then files per master are produced.

*Depends on:* TF-205, TF-206

**Notes:** Was FF-015.

#### BF-207 Use of master records by the products
*Priority:* **Must** · *Tag:* `[New]` · *Area:* Setup and data

**Requirement:** Business shall declare an extension point through which each product answers which of its live documents use a given master record; a master record in use shall be refused deletion and may be deactivated instead, and a change of a master's sharing mode shall list the documents of other organisations that use it (BF-004).

**Why:** Masters refuse deletion while documents use them (BF-072 AC3, BF-102 AC2) and list the documents a narrowed sharing mode affects (BF-004 AC2). The documents are the products', and Business never depends on a product (ADR-0042), so the products tell Business through a point it declares and they implement.

**Acceptance criteria:**

- AC1 Given a payment term used on an open order, When its deletion is requested, Then it is refused naming the order, and deactivation is offered. (error case)
- AC2 Given an item shared with organisations A and B and used on a request of B, When its sharing mode becomes private to A, Then the warning lists that request.
- AC3 Given a master record no product uses, When its deletion is requested, Then it is deleted.
- AC4 Given a product that does not answer, When the deletion of a master is requested, Then it is refused naming the product, not allowed. (error case)

*Depends on:* BF-004, BF-072, BF-102

**Notes:** Written at the checkpoint, after the design session's first answer: the one place where Business must hear from the products. Every product whose documents use masters implements it, and its Dependencies section says so (section 6.2).

## 6. Kernel extension points

### 6.1 What the kernel declares and this layer implements

The kernel declares these interfaces in its Contracts; this layer implements them (ADR-0029, decision 5).

| Interface | Declared in | What this layer supplies |
|---|---|---|
| `IOrganisationKinds` | `Iron.Kernel.Authorization.Contracts` (A1) | The kinds of unit: legal entity, business unit, department and team, with which may hold which (BF-006) |
| `IReviewEscalation` | `Iron.Kernel.Authorization.Contracts` (A7) | The reviewer's manager, from the people master, for an overdue access review (BF-026) |
| `IUnitReadiness` | `Iron.Kernel.Authorization.Contracts` (A8) | The readiness items of a unit: BF-202 and BF-203 |
| `IUnitRecordOwners` | `Iron.Kernel.Data` (A8) | The masters a unit owns, which move in a unit merge: every module of section 9 with owned records registers one |

### 6.2 What this layer declares and the products implement

Business never depends on a product (ADR-0042). Where it must hear from one, it declares an extension point in its Contracts and the product implements it; the product's Dependencies section says so.

| Extension point | Requirement | Who implements it |
|---|---|---|
| The use of master records: which live documents of a product use a given master | BF-207 | Every product whose documents use masters: Supplier management, Contracts, Catalogue, Requests, Orders, Receiving and Invoicing |

## 7. Rules and state machines

### 7.1 Key rules

From the archived PRD's section 7.1; each is also a requirement above or in a product.

| Rule | Statement |
|---|---|
| Account splits | Each split has an account string, a budget period when budget validation is on, and 1 to 100 percent; splits of a line total exactly 100 percent at submission; no duplicate account string and period on a line. |
| Budget availability | Available = amount + adjustments − consumed − reserved. Required per split = line amount × percent. Blocking or warning per document type. Reserve at submission, confirm at approval, release on rejection or cancellation. |
| Supplier uniqueness | Supplier number, tax identifiers, registration numbers and primary contact email unique; fuzzy duplicate warning on name and address. |
| Organisation visibility | Every record has an owning organisation and a sharing mode; users see records of organisations they have access to plus records shared with them or global; documents never mix organisations; a supplier must be enabled in the document's organisation. |

### 7.2 State machines

The masters have no state machine of their own in the archived PRD beyond active and inactive: budget periods are Open or Closed (BF-063), fiscal periods open or closed (BF-069), exchange rate revisions move through draft, submitted, approved and applied (BF-070).

## 8. Data and integration

### 8.1 Data notes

From the archived PRD's section 8.1: the largest tables of this layer (budget consumption) follow the kernel's partitioning conventions (TF-226) from the first migration; masters carry custom fields (TF-200), attachments (TF-160) and tracked change history (TF-062); master records are numbered at creation by the kernel's numbering (TF-166); sensitive supplier fields (bank account, tax identifiers) use field-level encryption (TF-074).

### 8.2 Integrations

| Direction | Data | Trigger | Kernel capability |
|---|---|---|---|
| Inbound | Legal entities, chart of accounts, account strings, budgets, currencies and rates, tax codes, units, categories, suppliers (optional master in ERP), payment terms | Schedule or push | Import framework, API, connectors (TF-205, TF-206) |

### 8.3 Reports

Budget consumption by line and document (BF-068).

## 9. Modules, schemas and permissions

Proposed by the placement test (ADR-0029): each holds masters or configuration that two or more products read, and none holds a product's documents. `[Decision]`: the modules and the permission names below, for the design session to confirm.

| Module | Holds | Schema | Requirements | Permissions proposed |
|---|---|---|---|---|
| `Iron.Business.Organisations` | The kinds of unit, legal entity attributes, purchasing groups, the organisation rules | `business_organisations` | BF-001 to BF-007, BF-203 | `organisations.legal_entities.{read,manage}`, `organisations.purchasing_groups.{read,manage}` |
| `Iron.Business.People` | People, managers, business groups, the escalation to a manager | `business_people` | BF-021 to BF-026 | `people.people.{read,manage}`, `people.groups.{read,manage}` |
| `Iron.Business.Items` | Items, categories and commodities, units of measure | `business_items` | BF-041 to BF-044 | `items.items.{read,manage}`, `items.categories.{read,manage}`, `items.units.{read,manage}` |
| `Iron.Business.Finance` | Charts of accounts, account strings, budgets, fiscal calendars, currencies and rates, tax codes with HSN and SAC, payment and shipping terms, projects, the budget reconciliation | `business_finance` | BF-061 to BF-075 | `finance.charts.{read,manage}`, `finance.budgets.{read,manage,adjust}`, `finance.rates.{read,manage,approve}`, `finance.taxes.{read,manage}`, `finance.terms.{read,manage}`, `finance.projects.{read,manage}` |
| `Iron.Business.Locations` | Ship-to, bill-to and warehouse locations | `business_locations` | BF-081 to BF-083 | `locations.locations.{read,manage}` |
| `Iron.Business.Suppliers` | The supplier record and its parts, its enablement per organisation | `business_suppliers` | BF-101 to BF-108 | `suppliers.suppliers.{read,create,update}`, `suppliers.bank_accounts.{read,update}`, `suppliers.enablements.{read,manage}` |
| `Iron.Business.Lookups` | The business lists and their seeds | `business_lookups` | BF-121, BF-122 | `lookups.values.manage` |
| `Iron.Business.Approvals` | Approval configurations, the delegation of authority matrix and its resolver, the starter set | `business_approvals` | BF-141 to BF-143, BF-201 | `approvals.configurations.{read,manage}`, `approvals.authority.{read,manage}` |
| `Iron.Business.Documents` | Document type settings, document templates, number series, notification templates and audiences | `business_documents` | BF-161 to BF-164 | `documents.settings.manage`, `documents.templates.manage`, `documents.series.manage`, `documents.notifications.manage` |
| `Iron.Business.Packs` | Country accelerator packs | `business_packs` | BF-204 | `packs.packs.install` (operators), `packs.packs.configure` |

Across the modules: each contributes its readiness items to the kernel's `IUnitReadiness` (BF-202, BF-203), carries its reference attributes (BF-205), declares its imports and exports (BF-206), declares the use-of-masters point for its masters (BF-207, one shape for all), and tags its masters (BF-181). Permission names name the module, not the layer (ADR-0029); the archived PRD's names (`finance.coa.*`, `suppliers.supplier.*`, `catalogue.item.*`) are replaced by these, since no permission of a business module exists yet.

## 10. Configuration

From the archived PRD's section 8.5: budget validation and blocking per document type; consumption triggers; multi-currency on lines; numbering per document and per organisation; document templates per organisation; approval configurations per document type and organisation (kernel workflow); country accelerator packs (Indian GST first). Each is a kernel setting (TF-057) declared by the module that owns it.

## 11. Slices

Where the moved requirements sat in the archived PRD's build plan (its section 9). The business slices are in the technical PRD's section 7.

| Slice of the archived plan | Its outcome | This document's requirements from it |
|---|---|---|
| P0 | Process reference packs | BF-205 |
| P1 | Masters, organisations and first request | BF-001, BF-002, BF-003, BF-004, BF-005, BF-042, BF-043, BF-061, BF-062, BF-063, BF-064, BF-065, BF-066, BF-067, BF-069, BF-070, BF-071, BF-072, BF-081, BF-108, BF-161, BF-202, BF-206 |
| P2 | Suppliers and orders | BF-101, BF-102, BF-103, BF-104, BF-105, BF-106, BF-162, BF-201, BF-203 |
| P3 | Receiving | BF-082 |
| P4 | Invoicing and matching | BF-068, BF-073 |
| P5 | Catalogue, contracts, change orders | BF-041, BF-044 |
| P6 | Completeness | BF-107, BF-204 |

## 12. Decisions and open points

### 12.1 Decisions

Confirmed on 2026-10-17, from the archived PRD's section 10.1, where they apply here:

| ID | Decision | Where applied |
|---|---|---|
| F-05 | Supplier bank changes need a second approver and an out-of-band verification checklist | BF-105, SUP-023 |
| F-08 | Tax codes with components; place-of-supply rule as a setting; e-invoice fields prepared; Indian GST as an accelerator pack | BF-071, BF-204, INV-010 |
| F-11 | Manual exchange rates with revision workflow; rate feed connector later | BF-070 |
| F-13 | Organisation (business unit) separation of master data, operations and visibility built into the functional foundation | Section 2.2, BF-001 to BF-005, BF-108, BF-203 |
| F-14 | One supplier registration per tenant with per-organisation enablement | BF-108 |
| F-15 | Intercompany documents not in version 1; design hooks kept | Section 2.3 |

The split itself is ADR-0041. Decided at its checkpoint, on 2026-10-20:

| Decision | Where applied |
|---|---|
| Business never depends on a product: a clause that belongs in a product moves there; where Business must hear from a product, it declares an extension point the product implements (ADR-0042) | BF-108 (its organisation approval is SUP-006), BF-202 and BF-204 (their product dependencies dropped; INV-010 depends on BF-204), BF-205 (its payment status codes are INV-045), BF-207 |
| Each module reconciles its own counters, in one nightly window of the kernel's jobs | BF-073 for the budgets |
| Approvals, tasks and notifications respect access | TF-243 |
| Bill-to is a location type in the locations master, linked to its legal entity, one default per legal entity and more allowed | BF-083 |

### 12.2 Open points

From the archived PRD's section 10.2:

| ID | Question | Default used |
|---|---|---|
| G-02 | Can a business unit without legal status raise documents, or only legal entities? | Any organisation can own documents; the legal entity above it supplies chart of accounts and tax registration |
| G-03 | Which organisation-level overrides are needed on day one beyond numbering, templates, approvals and tolerances? | Those four plus budget validation switches |
| G-04 | Intercompany documents in a later version: which flows (one organisation buying for another, internal recharge)? | Not in version 1; hooks kept on records |
| G-05 | Accelerator pack delivery: installed by operators per tenant, or chosen by tenant administrators from a catalogue? | Operators install; tenant administrators configure |

## 13. Glossary

| Term | Meaning |
|---|---|
| Organisation (business unit) | A sub-structure of the tenant that owns master data and documents and bounds visibility; a legal entity is an organisation with legal attributes |
| Supplier enablement | The record that makes a tenant-level supplier usable in one organisation, with that organisation's terms and status |
| Sharing mode | Private to the owning organisation, shared with listed organisations, or global to the tenant |
| Accelerator pack | Packaged configuration, reference data and rules for a country or industry (Indian GST first) |
| Account string | Coded combination of chart-of-account segment values used to code a line and track budget |
| Account split | Division of a line across account strings by percent, each with a budget period |
| Purchasing group | A group of buyers, the categories it covers and the units it serves; a business master, not a unit kind |
| Business group | A team of people that work and notifications are routed to; it gives no visibility and is not a role |
| Delegation of authority matrix | Who may approve up to what amount, by designation, job grade or role, category and legal entity or unit; distinct from the kernel's delegation of duties |
| Designation, job grade | A person's position and level, kept as business lookups; the grades in order |
| Cost object | Something spend is followed by beside the account string: a project here |
| Visibility tag | A tag that narrows which records a person sees (TF-241) |
