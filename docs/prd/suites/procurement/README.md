# The eProcurement Suite's modules

| Item | Value |
|---|---|
| What it holds | The suite's module list: each module, what it covers, how it is licensed, its wave, its document and the leaders' equivalent; what is deferred, and why; the research log of the decision |
| Decided | By the owner on 2026-10-29 in the design session, after a review of the module list against the leading suites with a global view (ADR-0058) |
| Written | 2026-10-30, task EPS-modules (`docs/prompts/EPS-modules.md`) |
| Not a requirements document | The requirements are in each module's PRD; a module with a scope page has none yet. `make prd-check` passes this file by. |

## 1. The modules

| # | Module | What it covers | Licensed as | Wave | Document | The leaders' equivalent |
|---|---|---|---|---|---|---|
| 1 | Purchasing | Purchasing groups, supplier and person purchasing profiles, the approval starter set, readiness | Shared, always on with the suite | 1 (V2 skeleton, then PR0) | `purchasing-PRD.md` | Set-up in each rather than a product: purchasing organisations and groups, and the supplier's purchasing data |
| 2 | Supplier Management | Onboarding, supplier information management (SIM), change management, qualification (GxP as a licensed feature), screening, performance | Module, with `supplier_management.gxp_qualification` | 1 | `supplier-management-PRD.md` | Coupa Supplier Management; SAP Ariba Supplier Lifecycle and Performance |
| 3 | Requests and Intake | Purchase requests, guided buying, intake as one front door routed to legal, security or IT, finance and procurement reviews; automatic orders; consolidation; central buying; routing rules | Module | 1 | `requests-PRD.md` | Coupa's requisitions and intake; SAP Ariba Buying with guided buying, and SAP Ariba Intake Management |
| 4 | Catalogue | Supplier catalogues, punch-out, marketplaces, contract items | Module | 1 | `catalogue-PRD.md` | Coupa's catalogues and punch-out; SAP Ariba Catalog and Spot Buy |
| 5 | Orders | Purchase orders, change orders, blanket orders, acknowledgements | Module | 1 | `orders-PRD.md` | Purchase orders in Coupa's procure-to-order and in SAP Ariba Buying, sent over their supplier networks |
| 6 | Receiving | Shipment notices, goods and service receipts, returns | Module | 1 | `receiving-PRD.md` | Receipts and ship notices in Coupa and in SAP Ariba Buying with the SAP Business Network |
| 7 | Invoicing | Capture, matching, approvals, credit notes; e-invoicing compliance per country as licensed features | Module, with a feature per country or regime, `invoicing.e_invoicing_<code>` | 1 | `invoicing-PRD.md` | Coupa's invoice-to-pay; SAP Ariba Invoice Management; each with e-invoicing compliance by country |
| 8 | Contracts | Repository, compliance, consumption, renewals; authoring, the clause library and negotiation as a later feature tier | Module, with the authoring tier as `contract_management.authoring` | 1, authoring in 2 | `contracts-PRD.md` | Coupa Contract Management; SAP Ariba Contracts |
| 9 | Spend Analytics and Savings | The spend cube and its classification (UNSPSC), dashboards, savings tracking from identification to realisation | Module | 2 | `spend-analytics-and-savings-scope.md` | Coupa's spend analysis; SAP Ariba Spend Analysis; GEP SMART's spend analysis and savings project tracking |
| 10 | Sourcing | RFI, RFP, RFQ, bid comparison, award scenarios; auctions as a licensed feature | Module, with auctions as `sourcing.auctions` | 2 | `sourcing-scope.md` (Auctions merged into it) | Coupa Sourcing; SAP Ariba Sourcing; each with auctions inside sourcing |
| 11 | Category and Savings Projects | Category strategies, sourcing and savings projects with their tasks | Module | 2 | `category-and-savings-projects-scope.md` (the projects scope, reframed) | Sourcing projects and category management in SAP Ariba and JAGGAER; GEP SMART's savings project management |
| 12 | Supplier Risk and Sustainability | Continuous risk monitoring, due-diligence questionnaires per law, corrective actions, diversity, emissions data | Module | 2 | `supplier-risk-and-sustainability-scope.md` | Coupa Risk Assess; SAP Ariba Supplier Risk |
| 13 | Payments | Payment files (ISO 20022), remittance advice, early-payment discounts, supplier finance through partners | Module | 3 | `payments-scope.md` | Coupa Pay; SAP Taulia for supplier finance |
| 14 | Services and Contingent Workforce | Statements of work, rate cards, timesheets, worker onboarding | Module | 3 | `services-and-contingent-workforce-scope.md` | SAP Fieldglass; Coupa's contingent workforce offering |
| 15 | Expenses | Travel and expense reports, receipts, policies, reimbursement | Module | 3 | `expenses-scope.md` | SAP Concur; Coupa Expenses |

The leaders' names are their own product names as their sites showed them on 2026-10-29 (vendor sources, section 8). GEP SMART and Ivalua, also reviewed, sell the same scope on one platform each; JAGGAER adds a focus on direct spend. A practitioner confirms each module's equivalents in its process reference pack (PM-002) before its first slice.

## 2. Licensing

- A module is licensed on its own in the suite's plan (TF-040), and a suite is installed only for the tenants that bought it (TF-244).
- Purchasing is the suite's shared master (ADR-0043): every plan of the suite includes it, since every other module reads it.
- A part sold separately is a feature of its module (TF-046), with a code `module.feature`, and is unavailable while its module is (TF-046 AC3). The features named so far:

| Feature | Module | What it adds | Requirements |
|---|---|---|---|
| `supplier_management.gxp_qualification` | Supplier Management | Regulated qualification of supplier, site and material | SUP-181 to SUP-184 |
| `invoicing.e_invoicing_<code>` | Invoicing | E-invoicing compliance for one country or regime, such as `invoicing.e_invoicing_pl` or `invoicing.e_invoicing_peppol` | INV-141 to INV-143, then the country's own |
| `contract_management.authoring` | Contracts | Authoring, the clause library and negotiation | CON-101 |
| `sourcing.auctions` | Sourcing | Live reverse auctions | Sourcing's scope, until its PRD is written |

## 3. Waves

A wave is an order of building. Each module's slice is a row of the technical PRD's section 7.2, which names what it needs.

| Wave | Modules | In section 7.2 |
|---|---|---|
| 1 | Purchasing, Supplier Management, Requests and Intake, Catalogue, Orders, Receiving, Invoicing, Contracts | PR0 to PR8; the suite's skeleton, the Purchasing module with its About page, comes first, in V2 (V's slice plan), so that suites per tenant (TF-244) have a module to install |
| 2 | Spend Analytics and Savings, Sourcing, Category and Savings Projects, Supplier Risk and Sustainability; Contracts' authoring tier | PR9 to PR13, after PR7 |
| 3 | Payments, Services and Contingent Workforce, Expenses | PR14 to PR16 |

## 4. The process order

The products of wave 1 depend on each other in the process order of ADR-0042: Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing. A new module takes its place when its PRD is written (ADR-0042, decision 8); each scope page proposes one:

| Module | Proposed place | Why |
|---|---|---|
| Supplier Risk and Sustainability | After Supplier management | It builds on Supplier management's information collection and screening, and the later products ask it for a supplier's risk |
| Sourcing | Between Requests and Orders, as decided on 2026-10-20 | It sources request lines, and an award becomes a contract or orders |
| Services and Contingent Workforce | Between Receiving and Invoicing | An approved timesheet becomes a service receipt in Receiving, which Invoicing then invoices |
| Payments | After Invoicing | It pays what Invoicing approved for payment |
| Spend Analytics and Savings | After Payments | It reads every earlier module's spend, and none of them calls it |
| Category and Savings Projects | Last | It calls Spend Analytics and Savings for a category's spend and savings, Sourcing for its events, Contracts and Supplier management |
| Expenses | Apart | It calls no procure-to-pay module |

## 5. Deferred, not dropped

| Module | Why it waits |
|---|---|
| Inventory and stockrooms | It serves manufacturing-heavy customers, and overlaps with the ERP systems that already hold their stock |
| Direct materials with bill-of-materials sourcing | The same customers and the same overlap; the first customers are indirect buyers in regulated industries |

Neither has a scope page or a prefix yet. Each gets both when a customer asks for it, by the placement test of ADR-0043.

## 6. What is not a module

- **The supplier portal** is a surface of the suite, not a module (ADR-0029): each module contributes its supplier views, which obey the portal's rules (`supplier-portal-PRD.md`, SPX-006).
- **Surveys** left the suite on 2026-10-29: the forms engine (TF-242, built in S4) runs surveys, sent to people inside or outside the tenant, for any suite. Procurement uses them through Supplier Management's information collection (SUP-221 to SUP-231) and Sourcing's evaluations. The prefix `SRV-` is retired (ADR-0058).

## 7. Requests and Intake's buying model

The owner decided on 2026-10-29 that Requests and Intake supports desktop buying, central buying and the mix between them, as configuration per legal entity, unit and category; the default is desktop buying with consolidation at the buying desk. At the review of 2026-10-30: the unit decides who may request; the category and the value decide how a line is fulfilled; when two settings disagree about fulfilment, the more controlled one wins, central over desktop and the buyer's queue over an automatic order. The requirements are in `requests-PRD.md`: intake (REQ-101, REQ-102), the buying model and central buying (REQ-121 to REQ-123), routing and automatic orders (REQ-141, REQ-142), and consolidation (REQ-161 to REQ-163); Orders makes the orders (ORD-161 to ORD-164).

## 8. Research log

Sources read by the design session on 2026-10-29, as its brief lists them. "Vendor" marks a vendor's own description of its products, primary for what the vendor sells and nothing more; "official" marks a law or a public authority's text; "third party" marks a report about either. Each module's process reference pack carries its own log (PM-003), and each country's e-invoicing feature confirms its dates against its tax authority when its requirements are written (INV-141).

| Source | Kind | Finding used |
|---|---|---|
| Coupa, the product menu on coupa.com, in its source-to-contract, procure-to-order and invoice-to-pay groups | Vendor | A leader's module boundaries: sourcing, contracts, supplier management and risk upstream; requisitions, intake, catalogues and orders in the middle; invoicing, payments and expenses as invoice-to-pay |
| SAP Ariba, its upstream modules (sourcing, contracts, supplier lifecycle and performance, supplier risk, spend analysis) and downstream modules (buying with guided buying, catalogues, invoice management, the SAP Business Network) | Vendor | The upstream and downstream split; spend analysis and supplier risk as modules of their own |
| SAP Ariba Intake Management | Vendor | One front door for any purchase need, with reviews routed before buying (REQ-101, REQ-102) |
| SAP Fieldglass and SAP Concur | Vendor | Services and contingent workforce, and travel and expenses, are sold as products beside the procurement suite (modules 14 and 15) |
| GEP SMART, its savings tracking | Vendor | Savings tracked from identification to realisation, beside spend analysis (module 9) |
| JAGGAER, its direct-spend and bill-of-materials focus | Vendor | Direct materials with bill-of-materials sourcing serve manufacturing-heavy buyers; deferred here |
| The e-invoicing mandates of 2026 to 2030: Belgium, Croatia, Malaysia, Brazil and Poland in early 2026; France from September 2026; Germany from 2027; the EU's VAT in the Digital Age (ViDA) in July 2030 | Official, as the dates were read on 2026-10-29 | Country e-invoicing compliance is a licensed feature of Invoicing per country or regime (INV-141 to INV-143) |
| ViDA, Council Directive (EU) 2025/516, https://eur-lex.europa.eu/eli/dir/2025/516/oj | Official | Digital reporting of intra-EU trade from July 2030, built on EN 16931 |
| The EU Deforestation Regulation, Regulation (EU) 2023/1115 as amended, https://eur-lex.europa.eu/eli/reg/2023/1115/oj; applying from 30 December 2026 | Official | Due diligence on supply chains of the commodities it names, with a due-diligence statement per placing on the market (Supplier Risk and Sustainability) |
| The EU forced labour regulation, Regulation (EU) 2024/3015, https://eur-lex.europa.eu/eli/reg/2024/3015/oj; applying from December 2027 | Official | Products made with forced labour may not be placed on the EU market or exported from it (Supplier Risk and Sustainability) |
| The Corporate Sustainability Due Diligence Directive, Directive (EU) 2024/1760 as amended, https://eur-lex.europa.eu/eli/dir/2024/1760/oj; the first companies from July 2028 | Official | Due diligence on the chain of activities, with corrective action plans (Supplier Risk and Sustainability) |
| The team's knowledge, not yet checked against a source in this log | Knowledge | Peppol BIS Billing 3.0 and EN 16931, Factur-X and UN/CEFACT CII, Poland's KSeF, Saudi Arabia's FATOORA, India's IRN, Malaysia's MyInvois (INV-142); ISO 20022 and the national payment rails (Payments); UNSPSC (Spend Analytics and Savings). To be cited when each module's or country's requirements are written |
