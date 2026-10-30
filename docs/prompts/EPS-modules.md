# EPS-modules: the eProcurement Suite's modules, and Requests and Intake

Written on 2026-10-29 in the design session (ADR-0023). The owner reviewed the eProcurement Suite's module list against the leading suites (Coupa, SAP Ariba, GEP SMART, JAGGAER, Ivalua) with a global view, and accepted it without change. He also decided that Requests and Intake supports desktop buying, central buying and the mix between them, as configuration. This task writes those decisions into the requirement documents. Documentation only.

## Opening message

```
Read CLAUDE.md, docs/STATUS.md, docs/adr/0041, 0042 and 0043, docs/prd/README.md, the suite's
documents in docs/prd/suites/procurement/ (all of them, the scopes included), section 7 and TF-242
of the technical PRD, and BF-007 of the Business PRD. Then read docs/prompts/EPS-modules.md and
follow it: branch slice/eps-modules from main, commit the prompt alone first, write a short plan
into the session, and work without waiting for approval unless the "When to stop" rule in CLAUDE.md
applies. Done when: the suite's module list with its waves, licensing and research log is written;
the four new modules have one-page scopes; Auctions is merged into Sourcing, the projects scope is
reframed, and Surveys leaves the suite for the forms engine; Invoicing has country e-invoicing
compliance as licensed features; Requests and Intake and Orders have the new requirements with
acceptance criteria; the technical PRD's section 7.2 places the new modules; make prd-check passes;
the change merges through a pull request. Finish with the report and its Risk section. Then stop.
```

## The module list (the owner's decision)

Write it as `docs/prd/suites/procurement/README.md` (or extend that file if it exists): a table with each module's name, what it covers, how it is licensed (a module, or a feature of another), its wave, its document, and the leaders' equivalent.

| # | Module | What it covers | Licensed as | Wave | Document |
|---|---|---|---|---|---|
| 1 | Purchasing | Purchasing groups, supplier and person purchasing profiles, approval starter set, readiness | Shared, always on with the suite | 1 (V2 skeleton, then PR0) | purchasing-PRD |
| 2 | Supplier Management | Onboarding, SIM, change management, qualification (GxP as a licensed feature), screening, performance | Module | 1 | supplier-management-PRD |
| 3 | Requests and Intake | Purchase requests, guided buying, intake as one front door routed to legal, security or finance reviews; automatic orders; consolidation; central buying; routing rules | Module | 1 | requests-PRD |
| 4 | Catalogue | Supplier catalogues, punch-out, marketplaces, contract items | Module | 1 | catalogue-PRD |
| 5 | Orders | Purchase orders, change orders, blanket orders, acknowledgements | Module | 1 | orders-PRD |
| 6 | Receiving | Shipment notices, goods and service receipts, returns | Module | 1 | receiving-PRD |
| 7 | Invoicing | Capture, matching, approvals, credit notes; e-invoicing compliance per country as licensed features | Module, with country features | 1 | invoicing-PRD |
| 8 | Contracts | Repository, compliance, consumption, renewals; authoring, clause library and negotiation as a later feature tier | Module, with the authoring tier as a feature | 1, authoring in 2 | contracts-PRD |
| 9 | Spend Analytics and Savings | Spend cube and classification (UNSPSC), dashboards, savings tracking from identification to realisation | Module | 2 | new scope |
| 10 | Sourcing | RFI, RFP, RFQ, bid comparison, award scenarios; auctions as a licensed feature | Module, auctions as a feature | 2 | sourcing-scope (auctions merged) |
| 11 | Category and Savings Projects | Category strategies, sourcing and savings projects with tasks | Module | 2 | reframed scope |
| 12 | Supplier Risk and Sustainability | Continuous risk monitoring, due-diligence questionnaires per law, corrective actions, diversity, emissions data | Module | 2 | new scope |
| 13 | Payments | Payment files (ISO 20022), remittance advice, early-payment discounts, supplier finance through partners | Module | 3 | new scope |
| 14 | Services and Contingent Workforce | Statements of work, rate cards, timesheets, worker onboarding | Module | 3 | new scope |
| 15 | Expenses | Travel and expense reports, receipts, policies, reimbursement | Module | 3 | expenses-scope |

Also record, with the reason for each:

- **Deferred, not dropped:** inventory and stockrooms; direct materials with bill-of-materials sourcing. Both serve manufacturing-heavy customers and overlap with ERP systems.
- **The supplier portal** is a surface of the suite, not a module (ADR-0029).
- **Surveys** leave the suite: the forms engine (TF-242, S4) gives every suite surveys, and procurement uses them through SIM and sourcing evaluations.

**The research log** of the decision, with sources read on 2026-10-29: Coupa's product menu (source-to-contract, procure-to-order, invoice-to-pay groups) on coupa.com; SAP Ariba's upstream and downstream modules, intake management, Fieldglass and Concur; GEP SMART's savings tracking; JAGGAER's direct-spend and bill-of-materials focus; the 2026 to 2030 e-invoicing mandates (Belgium, Croatia, Malaysia, Brazil and Poland in early 2026, France from September 2026, Germany from 2027, EU ViDA in July 2030); the EU deforestation rules (from 30 December 2026), the EU forced labour ban (from December 2027) and the amended CSDDD (first companies from July 2028). Mark vendor and third-party sources as such.

## Steps

1. **The prompt commit**, then the module list and its research log, as above.
2. **New one-page scopes,** in the same form as the existing scopes (purpose, users, main documents, the process in a few lines, the kernel and business requirements it will need, what is out of scope, open questions, and that its full PRD starts from its process reference pack):
   - `spend-analytics-and-savings-scope.md`, prefix `ANL-` reserved;
   - `supplier-risk-and-sustainability-scope.md`, prefix `RSK-` reserved, naming the laws above and building on Supplier Management's SIM and screening;
   - `payments-scope.md`, prefix `PAY-` reserved, naming ISO 20022 and the main national payment rails;
   - `services-and-contingent-workforce-scope.md`, prefix `SVC-` reserved, building on Receiving's service receipts.
3. **Merged, reframed and moved:**
   - `auctions-scope.md` is merged into `sourcing-scope.md` as a licensed feature of Sourcing. The `AUC-` prefix stays reserved and unused, with a note.
   - `projects-and-tasks-scope.md` becomes `category-and-savings-projects-scope.md`: category strategies, sourcing and savings projects with tasks. A project as a cost object stays in the Business PRD (BF-075).
   - `surveys-scope.md` is removed from the suite, with a note in the map. TF-242 gains one sentence: the forms engine also runs surveys, sent to people inside or outside the tenant, for any suite. The `SRV-` prefix is retired with its reason.
4. **Invoicing, country compliance.** New requirements in the Invoicing PRD:
   - e-invoicing compliance per country is a licensed feature of Invoicing (TF-046), one per country or regime;
   - the formats and models named as the families the product must support: Peppol BIS 3.0 and EN 16931, Factur-X and CII, the national clearance platforms (for example Poland's KSeF, Saudi Arabia's FATOORA, India's IRN, Malaysia's MyInvois);
   - connection through access points and partners where a country requires them, through the kernel's connectors (TF-206);
   - each country's own requirements are written when its feature is built.
5. **Contracts.** One requirement marks authoring, the clause library and negotiation as a wave-2 feature tier of Contracts.
6. **Requests and Intake, the owner's decisions** (module 3 keeps its name). New requirements with acceptance criteria, each with at least one error case:
   - **Intake:** one front door for any purchase need, by request type (catalogue item, free-text goods, services, software, contractors), with reviews routed by type and value to legal, security or IT, finance and procurement, before ordering.
   - **The buying model is configuration,** per legal entity, unit and category: desktop buying, central buying, or the mix. The default is desktop buying with consolidation at the buying desk.
   - **Automatic orders:** an approved line from a catalogue or a contract, with a known supplier and price, becomes an order without a buyer, by rules (category, amount limit, suppliers enabled for automatic orders).
   - **Consolidation across requests:** a buyer selects approved lines from many requests in the work queue and creates one order per supplier; automatic consolidation collects lines that share supplier, legal entity, currency and terms over a window the tenant sets (for example daily); each order line keeps its link to its request line, so every requester follows their own order, receipt and delivery; as an option, identical items become one order line with the total quantity, keeping each requester's share.
   - **Central buying:** a setting decides who may request (every employee, or only nominated requesters and buyers); a buyer records a request that arrived offline on behalf of a person or unit, with the original attached as evidence; demand can be imported in bulk (the kernel's import, S4) and consolidated the same way.
   - **Routing rules:** catalogue and contract lines go to automatic orders; other lines below a limit go to the buyer's queue; lines above a higher limit must be sourced first (with Sourcing, wave 2; until then they are flagged to the buyer).
   - Orders gains the matching requirements on its side: creating automatic and consolidated orders, by ADR-0042's rule (an automatic conversion is an event from Requests; a buyer's conversion on the Orders screen is a call into Requests).
7. **The technical PRD's section 7.2** places the new modules in their waves after PR7, and marks deferred modules as such.
8. `make prd-check`, then STATUS, the report, and the pull request.

Write `[Decision]` with a proposed default wherever a rule is needed that this file does not state (for example, the default consolidation window), and list every one in the report.

## Review checklist

- [ ] The module list matches the owner's decision, with waves, licensing and the research log.
- [ ] Four new scopes exist; Auctions is merged; the projects scope is reframed; Surveys has moved to the forms engine.
- [ ] Invoicing names country compliance as licensed features.
- [ ] Requests and Intake covers intake, the buying model as configuration, automatic orders, consolidation, central buying and routing rules, each with an error case.
- [ ] `make prd-check` passes.
