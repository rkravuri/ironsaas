# Requirements map: the Functional Foundation PRD to its new homes

| Item | Value |
|---|---|
| What it maps | Every requirement of the Functional Foundation PRD for procure-to-pay, version 1.2 (`archive/functional-foundation-p2p-PRD-v1.md`), to its new identifier and document (ADR-0041); and every requirement of the split's documents that moved after it (section "Moved after the split", ADR-0043) |
| Table | `requirements-map.csv` has the same rows; `make prd-check` holds the two equal and checks every row against the documents |
| Date | 2026-10-20 |
| Status | The placement the brief decided, the rows it did not settle placed by the test and marked, and the design session's answers at the checkpoint (section "Changed at the checkpoint") |

## How to read it

- **moved**: the requirement is in one new document under one new identifier, its words unchanged except for names that changed (ADR-0041, decision 7).
- **split**: its parts are several requirements, in one document or several; each part's notes say which part it holds.
- **merged**: it and others became one requirement.
- **covered**: a kernel requirement carries it (Covered by), built by the task named (Built by). Where it adds business rules beyond the kernel's, they are kept under the new identifier the row names.
- **retired**: it is gone, with the reason in the notes.
- **Placed by the test**: the brief's table did not settle the row, so the placement test of ADR-0029 did, for the reason in the notes.

Every new requirement says what it was at the start of its notes ("Was FF-022."), and its CSV row carries the old identifier in the column `was`; a part that moved after the split names every identifier it had ("Was FF-174 and BF-201."). TF identifiers do not change. The Supplier Network experience (FF-190 to FF-197) rests on the kernel's Supplier Network plane, TF-230 to TF-239.

Three moved requirements had an error case without the marker; their AC1 is now marked `(error case)`, as ADR-0041 asks: SUP-042 (was FF-031), REQ-006 (was FF-055) and INV-025 (was FF-121).

## Counts

After the review of L2 (section "Moved after the split"), eight rows that were moved are split.

| Outcome | FF identifiers |
|---|---|
| moved | 146 |
| split | 17 |
| merged | 0 |
| covered | 9 |
| retired | 0 |
| all | 172 |

| Document | Prefix | Requirements from the archived PRD |
|---|---|---|
| `foundation/business-foundation-PRD.md` | BF- | 39 |
| `product-method.md` | PM- | 5 |
| `suites/procurement/supplier-management-PRD.md` | SUP- | 18 |
| `suites/procurement/catalogue-PRD.md` | CAT- | 12 |
| `suites/procurement/requests-PRD.md` | REQ- | 14 |
| `suites/procurement/orders-PRD.md` | ORD- | 22 |
| `suites/procurement/receiving-PRD.md` | RCV- | 19 |
| `suites/procurement/invoicing-PRD.md` | INV- | 31 |
| `suites/procurement/contracts-PRD.md` | CON- | 9 |
| `suites/procurement/supplier-portal-PRD.md` | SPX- | 15 |
| `suites/procurement/purchasing-PRD.md` | PUR- | 8 |
| all | | 192 |

## Changed at the checkpoint

The design session answered the checkpoint on 2026-10-20, and five rows changed:

- FF-182: split; its optional organisation approval moved from the Business PRD to Supplier management (SUP-006), since Business never depends on a product (ADR-0042).
- FF-202: split; its ISO 20022 payment status codes moved from the Business PRD to Invoicing (INV-045), for the same reason.
- FF-133: split four ways; each module reconciles its own counters in one nightly window (INV-027, ORD-121, RCV-083, BF-073).
- FF-036: moved from Supplier management to the supplier portal surface (SPX-006), as a rule every product's supplier views obey. It was SUP-081 in the checkpoint's skeleton; that identifier is not reused.
- FF-184: its clause on approvals, tasks and notifications is the new kernel requirement TF-243.

Dependencies changed with ADR-0042 too; each requirement whose dependencies changed says why in its notes.

## Rows placed by the test

- FF-049 Catalogue and item imports: the brief puts FF-041 to FF-049 in Catalogue, but items are the Business item master (BF-041), and the import of a master belongs with it; supplier items stay in Catalogue.
- FF-160 Operational reports: Placed by the test beyond the brief's Orders and Invoicing: section 8.4 of the archived PRD also names the requests, receipts and catalogue reports, and each product owns its reports. Budget, supplier and contract reports were already FF-162, FF-163 and FF-146.

## Criteria written for a split

Where a part of a split kept no criterion of the original that tests it, or none marked as an error case, a criterion was written for the part from the requirement's own clauses; the part's notes say so.

- BF-103 AC1 (from FF-027): Given a network user of the supplier organisation, When the supplier is opened in the tenant, Then the user is listed as a linked contact with name, email, phone and role.
- BF-103 AC2 (from FF-027): Given a buyer tries to set a password for a supplier contact, When saved, Then it is refused because the tenant never holds supplier credentials. (error case)
- BF-105 AC1 (from FF-029): Given a bank account saved, When the database is read without the application, Then the account number is unreadable.
- BF-105 AC2 (from FF-029): Given a bank account whose verification is not complete, When it is chosen for a payment instruction, Then it is refused. (error case)
- CAT-006 AC1 (from FF-049): Given a valid supplier item file, When imported, Then supplier items are created for their items and suppliers.
- CAT-007 AC2 (from FF-186): Given a catalogue item published only to organisation A, When a user of organisation B requests it by identifier, Then it is refused. (error case)
- SUP-006 AC1 (from FF-182): Given organisation approval required in organisation A, When a supplier is enabled in A, Then the enablement takes effect when the approval completes.
- SUP-006 AC2 (from FF-182): Given an enablement in organisation A still in approval, When a buyer of A creates an order for the supplier, Then it is refused naming the pending approval. (error case)
- INV-045 AC2 (from FF-202): Given a payment status message whose code is not an ISO 20022 payment status code, When received, Then it is rejected naming the code. (error case)
- BF-203 AC2 (from FF-188): Given a user without administration permission, When they open an organisation's readiness checklist, Then it is refused. (error case)

## The map

| Old | Title | Outcome | New | Documents | Covered by | Built by | Placed by the test | Notes |
|---|---|---|---|---|---|---|---|---|
| FF-001 | Legal entity finance attributes | moved | BF-001 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-002 | Chart of accounts and segments | split | BF-061, PUR-001, PUR-025 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): the buying group to a legal entity's purchasing group (PUR-001), the default match levels to the purchasing master (PUR-025). |
| FF-003 | Account strings | moved | BF-062 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-004 | Budget periods | moved | BF-063 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-005 | Budget lines | moved | BF-064 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-006 | Budget availability check with reservation | moved | BF-065 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-007 | Budget consumption and release | moved | BF-066 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-008 | Budget adjustments and transfers | moved | BF-067 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-009 | Fiscal calendar | moved | BF-069 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-010 | Categories and commodities | moved | BF-042 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-011 | Currencies and exchange rates with revisions | moved | BF-070 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-012 | Tax codes and classifications | moved | BF-071 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-013 | Units of measure | moved | BF-043 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-014 | Payment and shipping terms | moved | BF-072 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-015 | Masters import, export and ERP sync | moved | BF-206 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-020 | Supplier invitation | moved | SUP-001 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-021 | Supplier self-registration | moved | SUP-002 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-022 | Supplier master | split | BF-101, PUR-021 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The supplier record every product reads; its life cycle is Supplier management. At the review of L2 (ADR-0043, decision 8): its match levels, portal channels and preferred flag to the supplier purchasing profile (PUR-021). |
| FF-023 | Duplicate supplier detection | moved | SUP-003 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-024 | Supplier approval and publishing | moved | SUP-004 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-025 | Supplier status control | moved | SUP-021 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-026 | Supplier addresses | moved | BF-102 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-027 | Supplier contacts and portal users | split | BF-103, SPX-005 | `foundation/business-foundation-PRD.md`, `suites/procurement/supplier-portal-PRD.md` |  |  |  | The contact data to Business; the portal users follow the supplier portal surface, as the brief says. |
| FF-028 | Supplier sites | moved | BF-104 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-029 | Supplier bank accounts with verification | split | BF-105, SUP-023 | `foundation/business-foundation-PRD.md`, `suites/procurement/supplier-management-PRD.md` |  |  |  | The account data to Business, its verification to Supplier management, as the brief says. |
| FF-030 | Supplier documents and compliance expiry | moved | SUP-041 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-031 | Supplier questionnaires | moved | SUP-042 | `suites/procurement/supplier-management-PRD.md` |  |  |  | On the kernel's forms engine (TF-242); AC1 marked as the error case it is. |
| FF-032 | Supplier performance indicators | moved | SUP-062 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-033 | Supplier change approval | moved | SUP-022 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-034 | Supplier 360 view and lists | moved | SUP-061 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-035 | Supplier configuration defaults | split | BF-106, PUR-024, SUP-005, SPX-007 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md`, `suites/procurement/supplier-management-PRD.md`, `suites/procurement/supplier-portal-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): payment terms and currency stay; the match levels to PUR-024, the required sections and documents to SUP-005, the portal channels to SPX-007. |
| FF-036 | Supplier portal ownership | moved | SPX-006 | `suites/procurement/supplier-portal-PRD.md` |  |  |  | Moved to the supplier portal surface by the design session's answer at the checkpoint: a rule every product's supplier views obey. It was SUP-081 in the checkpoint's skeleton; that identifier is not reused. |
| FF-037 | Supplier merge | moved | SUP-024 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-038 | Supplier categories and preferred status | split | BF-107, PUR-022 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): the categories a supplier serves stay; preferred status and suggestions to PUR-022. |
| FF-039 | Supplier risk flags | moved | SUP-043 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-040 | Items | moved | BF-041 | `foundation/business-foundation-PRD.md` |  |  |  | The item master; supplier items stay in Catalogue. |
| FF-041 | Supplier items | moved | CAT-001 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-042 | Catalogue upload and revisions | moved | CAT-002 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-043 | Catalogue revision acceptance | moved | CAT-003 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-044 | Catalogue publishing | moved | CAT-004 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-045 | Browse and cart | moved | CAT-021 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-046 | Contract items in the catalogue | moved | CAT-022 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-047 | Price validity and currency rules | moved | CAT-005 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-048 | Catalogue search and favourites | moved | CAT-023 | `suites/procurement/catalogue-PRD.md` |  |  |  |  |
| FF-049 | Catalogue and item imports | split | BF-044, CAT-006 | `foundation/business-foundation-PRD.md`, `suites/procurement/catalogue-PRD.md` |  |  | yes | Placed by the test: the brief puts FF-041 to FF-049 in Catalogue, but items are the Business item master (BF-041), and the import of a master belongs with it; supplier items stay in Catalogue. |
| FF-050 | Create and edit a request | moved | REQ-001 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-051 | Request lines and lots | moved | REQ-002 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-052 | Account splits on lines | moved | REQ-003 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-053 | Line currencies and conversion | moved | REQ-004 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-054 | Submission validations | moved | REQ-005 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-055 | Budget check at submission | moved | REQ-006 | `suites/procurement/requests-PRD.md` |  |  |  | AC1 marked as the error case it is. |
| FF-056 | Approval of requests | moved | REQ-007 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-057 | Cancel a request | moved | REQ-008 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-058 | Convert a request to orders | moved | REQ-021 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-059 | Partial ordering and close-short | moved | REQ-022 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-060 | Copy and templates | moved | REQ-041 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-061 | Request lists and visibility | moved | REQ-042 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-062 | Ship-to locations | moved | BF-081 | `foundation/business-foundation-PRD.md` |  |  |  | Locations are Business. |
| FF-063 | Request totals and summary | moved | REQ-009 | `suites/procurement/requests-PRD.md` |  |  |  |  |
| FF-070 | Create orders | moved | ORD-001 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-071 | Order lines and totals | moved | ORD-002 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-072 | Order splits and budget | moved | ORD-003 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-073 | Order submission and approval | moved | ORD-004 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-074 | Match level and tolerance stamping | moved | ORD-005 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-075 | Issue orders by channel | moved | ORD-006 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-076 | Supplier acknowledgement | moved | ORD-021 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-077 | Acknowledgement reminders | moved | ORD-022 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-078 | Order lists and views | moved | ORD-061 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-079 | Change orders with versions | moved | ORD-023 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-080 | Hold and release | moved | ORD-041 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-081 | Soft close, close and cancel | moved | ORD-042 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-082 | Line closure flags and close-short | moved | ORD-043 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-083 | Contract reference on order lines | moved | ORD-008 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-084 | Order totals, taxes and documents | moved | ORD-007 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-085 | Requester confirmation and notifications | moved | ORD-009 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-086 | Blanket and standing orders | moved | ORD-062 | `suites/procurement/orders-PRD.md` |  |  |  |  |
| FF-090 | Shipment notices (ASN) | moved | RCV-001 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-091 | Shipment notice visibility and tracking | moved | RCV-002 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-092 | Create a receipt | moved | RCV-021 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-093 | Receipt tolerance | moved | RCV-022 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-094 | Inspection and warehouse | moved | RCV-023 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-095 | Receipt submission and approval | moved | RCV-024 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-096 | Receipt rejection and cancellation | moved | RCV-025 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-097 | Returns to supplier | moved | RCV-061 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-098 | Received and invoiced buckets | moved | RCV-081 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-099 | Open quantities view | moved | RCV-082 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-100 | Warehouses and locations | moved | BF-082 | `foundation/business-foundation-PRD.md` |  |  |  | Locations are Business. |
| FF-101 | Receipt documents and print | moved | RCV-026 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-102 | Service receipts | moved | RCV-041 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-103 | Receiving delegation and requester receipts | moved | RCV-027 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-104 | Receipt lists and supplier view | moved | RCV-028 | `suites/procurement/receiving-PRD.md` |  |  |  |  |
| FF-110 | Invoice types | moved | INV-001 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-111 | Create invoice from orders | moved | INV-002 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-112 | Create invoice from receipts | moved | INV-003 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-113 | Non-order invoices | moved | INV-004 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-114 | Proforma invoices | moved | INV-005 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-115 | Invoice lines, taxes, charges, discounts and withholding | moved | INV-006 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-116 | Account coding for invoice lines | moved | INV-007 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-117 | Capacity checks at submission | moved | INV-021 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-118 | Variance tolerances and holds | moved | INV-022 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-119 | Duplicate invoice check | moved | INV-023 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-120 | Stale match level and refresh | moved | INV-024 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-121 | Budget check for non-order invoices | moved | INV-025 | `suites/procurement/invoicing-PRD.md` |  |  |  | AC1 marked as the error case it is. |
| FF-122 | Invoice approval and disputes | moved | INV-041 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-123 | Cancel an invoice | moved | INV-042 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-124 | Payment release and payment status | moved | INV-043 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-125 | Payment terms, due dates and discounts | moved | INV-008 | `suites/procurement/invoicing-PRD.md` |  |  |  | Keeps the due-date rules; the payment terms master is BF-072. |
| FF-126 | Invoice lists, queue and summaries | moved | INV-081 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-127 | Credit notes | moved | INV-044 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-128 | Invoice attachments and capture suggestions | moved | INV-009 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-129 | E-invoicing readiness | moved | INV-010 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-130 | Accrual report | moved | INV-082 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-131 | Uninvoiced and open-order views | moved | INV-083 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-132 | Matching configuration | moved | INV-061 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-133 | Nightly reconciliation of buckets and budgets | split | INV-027, ORD-121, RCV-083, BF-073 | `suites/procurement/invoicing-PRD.md`, `suites/procurement/orders-PRD.md`, `suites/procurement/receiving-PRD.md`, `foundation/business-foundation-PRD.md` |  |  |  | Split by the design session's answer at the checkpoint: each module reconciles its own counters in its own requirement, all in one nightly window of the kernel's jobs (TF-120 to TF-124). |
| FF-134 | Holds console | moved | INV-026 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-140 | Contract repository | moved | CON-001 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-141 | Contract items and pricing | moved | CON-002 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-142 | Contract approval and publishing | moved | CON-003 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-143 | Contract consumption | moved | CON-021 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-144 | Expiry and renewal alerts | moved | CON-022 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-145 | Contract documents and clauses | moved | CON-004 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-146 | Contract reports | moved | CON-041 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-147 | Contract import | moved | CON-005 | `suites/procurement/contracts-PRD.md` |  |  |  |  |
| FF-150 | Supplier portal access through the network | moved | SPX-001 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-151 | Orders and acknowledgement in the portal | moved | ORD-101 | `suites/procurement/orders-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-152 | Shipment notices in the portal | moved | RCV-121 | `suites/procurement/receiving-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-153 | Company data in the portal | moved | SUP-101 | `suites/procurement/supplier-management-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-154 | Receipts and returns in the portal | moved | RCV-122 | `suites/procurement/receiving-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-155 | Invoices in the portal | moved | INV-101 | `suites/procurement/invoicing-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-156 | Catalogue in the portal | moved | CAT-061 | `suites/procurement/catalogue-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-157 | Questionnaires and documents in the portal | moved | SUP-102 | `suites/procurement/supplier-management-PRD.md` |  |  |  | A supplier view, in the product it belongs to. |
| FF-158 | Tasks and queries in the portal | moved | SPX-003 | `suites/procurement/supplier-portal-PRD.md` |  |  |  | Uses the kernel's tasks (TF-097) when they exist. |
| FF-159 | Portal home | moved | SPX-002 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-160 | Operational reports | split | REQ-061, ORD-081, RCV-101, INV-084, CAT-041 | `suites/procurement/requests-PRD.md`, `suites/procurement/orders-PRD.md`, `suites/procurement/receiving-PRD.md`, `suites/procurement/invoicing-PRD.md`, `suites/procurement/catalogue-PRD.md` |  |  | yes | Placed by the test beyond the brief's Orders and Invoicing: section 8.4 of the archived PRD also names the requests, receipts and catalogue reports, and each product owns its reports. Budget, supplier and contract reports were already FF-162, FF-163 and FF-146. |
| FF-161 | Cycle time and first-pass match measures | split | ORD-082, INV-085 | `suites/procurement/orders-PRD.md`, `suites/procurement/invoicing-PRD.md` |  |  |  | Split between Orders and Invoicing, as the brief says: the order cycle times to Orders, the invoice measures to Invoicing. |
| FF-162 | Budget reports | moved | BF-068 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-163 | Supplier reports | moved | SUP-063 | `suites/procurement/supplier-management-PRD.md` |  |  |  |  |
| FF-164 | Analytics data feed | covered |  |  | TF-089, TF-203 | not built yet: TF-089 is S4, TF-203 S5 |  | Each product declares what it feeds (section 11.4 of each product PRD). |
| FF-165 | Document search | covered |  |  | TF-204 | not built yet: S5 |  | Each product declares what it indexes and by which fields (section 11.4 of each product PRD). |
| FF-166 | Exports for audit and ERP | covered |  |  | TF-203, TF-205, TF-206 | not built yet: TF-203 and TF-205 are S5, TF-206 S7; the audit export TF-064 is S3 |  | Each product declares its audit and ERP exports (section 11.4 of each product PRD). |
| FF-170 | Document type settings | split | BF-161, ORD-022 | `foundation/business-foundation-PRD.md`, `suites/procurement/orders-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): the acknowledgement reminder days to Orders (ORD-022). |
| FF-171 | Supplier onboarding configuration | split | SUP-005, SPX-007 | `suites/procurement/supplier-management-PRD.md`, `suites/procurement/supplier-portal-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): its portal channels to the supplier portal (SPX-007). |
| FF-172 | Matching and tolerance configuration screen | moved | INV-062 | `suites/procurement/invoicing-PRD.md` |  |  |  |  |
| FF-173 | Document templates for the product | split | BF-162, ORD-141, RCV-141, INV-121 | `foundation/business-foundation-PRD.md`, `suites/procurement/orders-PRD.md`, `suites/procurement/receiving-PRD.md`, `suites/procurement/invoicing-PRD.md` |  |  |  | At the review of L2 (ADR-0043, decision 8): the template mechanism stays; each product names its own templates. |
| FF-174 | Approval configuration starter set | moved | PUR-061 | `suites/procurement/purchasing-PRD.md` |  |  |  | Moved at the review of L2 (ADR-0043, decision 8), as BF-201 was: a starter set is a suite's content. |
| FF-175 | Go-live checklist and data readiness | split | BF-202, PUR-081 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | Its checklist is contributions to the kernel's IUnitReadiness (A8); its dependency on the supplier onboarding configuration is dropped (ADR-0042). At the review of L2 (ADR-0043, decision 8): the eProcurement Suite's items to PUR-081. |
| FF-176 | Country accelerator packs | moved | BF-204 | `foundation/business-foundation-PRD.md` |  |  |  | Its dependency on e-invoicing readiness is reversed: INV-010 depends on it (ADR-0042). |
| FF-180 | Owning organisation on every record | covered | BF-003 | `foundation/business-foundation-PRD.md` | TF-220 | S1 (the owning organisation on every business record), A1 (an active unit), A3 (the unit policy on lists and reads), A8 (sharing modes) |  | The owning organisation and the sharing mode are the kernel's; BF-003 keeps the business rules FF-180 adds: which records carry them, documents take the organisation of the requester or buyer and never mix organisations, the organisation shows on lists and records. |
| FF-181 | Sharing modes for master data | covered | BF-004 | `foundation/business-foundation-PRD.md` | TF-220 | A8 (sharing modes, the shares in kernel_data) |  | The three sharing modes are the kernel's; BF-004 keeps the default mode per master type and what a change of mode does to documents that reference the record. |
| FF-182 | Supplier enablement per organisation | split | BF-108, SUP-006, PUR-023 | `foundation/business-foundation-PRD.md`, `suites/procurement/supplier-management-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | Split by the design session's answer at the checkpoint (Business never depends on a product, ADR-0042): the enablement and its terms to Business, its optional organisation approval to Supplier management. Its clause that supplier users see the documents of every organisation that enabled them, with its AC3, is SPX-004's, which says the same (was FF-189). At the review of L2 (ADR-0043, decision 8): its match levels and preferred flag per organisation to PUR-023. |
| FF-183 | Organisation-specific configuration | covered | BF-005 | `foundation/business-foundation-PRD.md` | TF-221 | A6 (settings with the unit scope); numbering and templates are S4, approval configurations S5 |  | The organisation scope of configuration is the kernel's; BF-005 keeps which business configuration is set per organisation (open point G-03). |
| FF-184 | Document visibility by organisation | covered |  |  | TF-222, TF-035, TF-243 | A3 (record policies on reads and lists), A8 (cross-unit access, the read audit, the working unit); TF-243 is not built yet (with the workflow engine, S5) |  | Nothing business left over; each product's record policies apply it. Its clause that approvals, tasks and notifications respect the same access is TF-243, which the design session added at the checkpoint. |
| FF-185 | Finance masters per legal entity and organisation | moved | BF-002 | `foundation/business-foundation-PRD.md` |  |  |  |  |
| FF-186 | Catalogue and contract applicability per organisation | split | CAT-007, CON-006 | `suites/procurement/catalogue-PRD.md`, `suites/procurement/contracts-PRD.md` |  |  |  | Split between Catalogue and Contracts, as the brief says. |
| FF-187 | Consolidated reporting and organisation switcher | covered |  |  | TF-222 | A8 (the working unit with All my units, consolidated views by a parent's scope) |  | Nothing business left over: per-organisation and consolidated reports are TF-222's consolidated mode, which each product's reports follow. |
| FF-188 | Organisation onboarding and lifecycle | covered | BF-203, PUR-081 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` | TF-223 | A8 (create with readiness from modules, deactivate, reactivate, merge with a preview) |  | Deactivating and merging organisations are the kernel's; BF-203 keeps the checklist's items, which this layer contributes to the kernel's IUnitReadiness. At the review of L2 (ADR-0043, decision 8): its enabled suppliers are the eProcurement Suite's item (PUR-081). |
| FF-189 | Supplier portal across organisations | moved | SPX-004 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-190 | Supplier connection and onboarding through the network | moved | SPX-021 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-191 | Unified inbox across customers | moved | SPX-022 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-192 | Customer switcher and customer context | moved | SPX-023 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-193 | Acting on documents of several customers | moved | SPX-024 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-194 | One profile, per-customer acceptance | moved | SPX-025 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-195 | Supplier users and roles in the network | moved | SPX-026 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-196 | Supplier notifications across customers | moved | SPX-027 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-197 | Relationship status and history | moved | SPX-028 | `suites/procurement/supplier-portal-PRD.md` |  |  |  |  |
| FF-200 | Process reference pack per module | moved | PM-001 | `product-method.md` |  |  |  |  |
| FF-201 | Standards alignment of document models | moved | PM-021 | `product-method.md` |  |  |  |  |
| FF-202 | Reference data from standards | split | BF-205, INV-045 | `foundation/business-foundation-PRD.md`, `suites/procurement/invoicing-PRD.md` |  |  |  | Split by the design session's answer at the checkpoint (Business never depends on a product, ADR-0042): the masters' reference data to Business, the ISO 20022 payment status codes to Invoicing. |
| FF-203 | Benchmark indicators built in | moved | PM-022 | `product-method.md` |  |  |  |  |
| FF-204 | External practitioner review | moved | PM-002 | `product-method.md` |  |  |  |  |
| FF-205 | Research log and living process documentation | moved | PM-003 | `product-method.md` |  |  |  |  |

## Moved after the split

At the review of L2 on 2026-10-20 the design session decided that the Foundation holds mechanisms and generic masters, and a suite brings its own content (ADR-0043, decision 8). These requirements of the split's documents moved, whole or in part; each part that moved keeps its old identifiers at the start of its notes and in the column `was`, and a moved identifier is never reused. The table has the same columns as the map above, and the same CSV.

| Old | Title | Outcome | New | Documents | Covered by | Built by | Placed by the test | Notes |
|---|---|---|---|---|---|---|---|---|
| BF-007 | Purchasing groups | moved | PUR-001 | `suites/procurement/purchasing-PRD.md` |  |  |  | Only the eProcurement Suite assigns buyers. |
| BF-021 | People master | split | BF-021, PUR-041 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The default ship-to location to the person's purchasing profile; the default account string stays. |
| BF-061 | Chart of accounts and segments | split | BF-061, PUR-001, PUR-025 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The buying group merged into purchasing groups; the default match levels to the purchasing master. |
| BF-101 | Supplier master | split | BF-101, PUR-021 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | Match levels, portal channels and the preferred flag to the supplier purchasing profile. |
| BF-106 | Supplier configuration defaults | split | BF-106, PUR-024, SUP-005, SPX-007 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md`, `suites/procurement/supplier-management-PRD.md`, `suites/procurement/supplier-portal-PRD.md` |  |  |  | Payment terms and currency stay; match levels, required sections and documents, and portal channels go to their owners. |
| BF-107 | Supplier categories and preferred status | split | BF-107, PUR-022 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The categories stay; preferred status and suggestions move. BF-107 is now titled Supplier categories. |
| BF-108 | Supplier enablement per organisation | split | BF-108, PUR-023 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The match levels and preferred flag of an organisation's terms. |
| BF-161 | Document type settings | split | BF-161, ORD-022 | `foundation/business-foundation-PRD.md`, `suites/procurement/orders-PRD.md` |  |  |  | The acknowledgement reminder days. |
| BF-162 | Document templates for the products | split | BF-162, ORD-141, RCV-141, INV-121 | `foundation/business-foundation-PRD.md`, `suites/procurement/orders-PRD.md`, `suites/procurement/receiving-PRD.md`, `suites/procurement/invoicing-PRD.md` |  |  |  | The mechanism stays; each product names its templates. BF-162 is now titled Document templates. |
| BF-164 | Business notification templates and audiences | split | BF-164, SUP-121, CON-081, CAT-081, REQ-081, ORD-142, RCV-142, INV-122 | `foundation/business-foundation-PRD.md`, `suites/procurement/supplier-management-PRD.md`, `suites/procurement/contracts-PRD.md`, `suites/procurement/catalogue-PRD.md`, `suites/procurement/requests-PRD.md`, `suites/procurement/orders-PRD.md`, `suites/procurement/receiving-PRD.md`, `suites/procurement/invoicing-PRD.md` |  |  |  | The mechanism stays; each product declares its keys and starter templates. |
| BF-201 | Approval configuration starter set | moved | PUR-061 | `suites/procurement/purchasing-PRD.md` |  |  |  | A starter set is a suite's content; BF-141 keeps the mechanism. |
| BF-202 | Go-live checklist and data readiness | split | BF-202, PUR-081 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The view stays; the suite's items, its suppliers and items among them, move. |
| BF-203 | Organisation readiness checklist | split | BF-203, PUR-081 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  | The checklist stays; its enabled suppliers are the suite's item. |
| SUP-005 | Supplier onboarding configuration | split | SUP-005, SPX-007 | `suites/procurement/supplier-management-PRD.md`, `suites/procurement/supplier-portal-PRD.md` |  |  |  | Its portal channels to the supplier portal, beside BF-106's. |
