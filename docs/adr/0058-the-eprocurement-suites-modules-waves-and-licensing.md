# ADR-0058: The eProcurement Suite's modules, their waves and their licensing

**Status:** Accepted
**Date:** 2026-10-30
**Decided by:** Product owner and architect (the module list and the buying model on 2026-10-29, in the design session; accepted at the review of EPS-modules, 2026-10-30)

## Context

The PRD split (ADR-0041) left the eProcurement Suite with seven products, a shared master (ADR-0043), the supplier portal surface and five one-page scopes: Sourcing, Auctions, Projects and tasks, Surveys and Expenses, with the prefixes `SRC-`, `AUC-`, `PRJ-`, `SRV-` and `EXP-` reserved. That list came from the current product, not from a view of the market. On 2026-10-29 the owner reviewed it against the leading suites (Coupa, SAP Ariba, GEP SMART, JAGGAER and Ivalua) with a global view, and against the laws that now reach into a buyer's supply chain: the e-invoicing mandates of 2026 to 2030 and the EU's due-diligence rules. Three gaps stood out: no front door for a purchase need before it becomes a request, no spend analysis or savings tracking, and no module for supplier risk beyond Supplier management's flags. The owner also decided that Requests and Intake supports desktop buying, central buying and the mix between them, as configuration. Task EPS-modules (`docs/prompts/EPS-modules.md`) writes these decisions into the requirement documents.

## Decision

1. **Fifteen modules.** The suite's module list is `docs/prd/suites/procurement/README.md`, with each module's scope, licensing, wave, document and the leaders' equivalent. It is the source; this ADR records why.
2. **A module each, features within.** Every module is licensed on its own in the suite's plan (TF-040, TF-244), except Purchasing, the shared master, which every plan of the suite includes. A part sold separately is a feature of its module (TF-046), with a code `module.feature`:
   - `sourcing.auctions`: auctions are a kind of sourcing event, not a module.
   - `contract_management.authoring`: authoring, the clause library and negotiation, a wave-2 tier of Contracts (CON-101).
   - `invoicing.e_invoicing_<country or regime>`: e-invoicing compliance, one feature per country or regime (INV-141).
   - `supplier_management.gxp_qualification`, as SUP-gaps named it.
3. **Three waves.** Wave 1 is the slices PR0 to PR8 of the technical PRD's section 7.2, with the eProcurement Suite's skeleton, the Purchasing module with its About page, in V2 so that suites per tenant have a module to install. Wave 2 is Spend Analytics and Savings, Sourcing, Category and Savings Projects, Supplier Risk and Sustainability, and Contracts' authoring tier. Wave 3 is Payments, Services and Contingent Workforce, and Expenses. Section 7.2 places them after PR7.
4. **Deferred, not dropped:** inventory and stockrooms, and direct materials with bill-of-materials sourcing. Both serve manufacturing-heavy customers and overlap with ERP systems.
5. **Not modules.** The supplier portal is a surface of the suite (ADR-0029, ADR-0042 decision 5). Surveys leave the suite: the forms engine (TF-242, S4) runs surveys for every suite, and procurement uses them through supplier information collection and sourcing evaluations.
6. **Prefixes.** `ANL-`, `RSK-`, `PAY-` and `SVC-` are reserved for the four new modules. `AUC-` stays reserved and is never used: auctions' requirements take `SRC-`. `PRJ-` stays with the reframed Category and Savings Projects. `SRV-` is retired, never given to another document. `SRC-` and `EXP-` are unchanged. This replaces the last row of ADR-0041's decision 1.
7. **Requests and Intake's buying model is configuration,** per legal entity, unit and category: desktop buying, central buying, or the mix. The default is desktop buying with consolidation at the buying desk. At the review (2026-10-30): the unit decides who may request; the category and the value decide how a line is fulfilled; when two settings disagree about fulfilment, the more controlled one wins, central over desktop and the buyer's queue over an automatic order (REQ-121, REQ-122). Conversions follow ADR-0042: an automatic order or an automatic consolidation is an event from Requests that Orders handles; a buyer's conversion on Orders' screen is a call into Requests.
8. **Places in the process order** (ADR-0042, decision 8), proposed by each scope and fixed when its PRD is written: Supplier Risk and Sustainability after Supplier management; Sourcing between Requests and Orders, as decided on 2026-10-20; Services and Contingent Workforce between Receiving and Invoicing; Payments after Invoicing; Spend Analytics and Savings after Payments; Category and Savings Projects last; Expenses apart, since it calls no procure-to-pay product.

## Options considered

- Keep the five scopes as they were. Rejected: a buyer of a source-to-pay suite expects intake, spend analysis, savings tracking and supplier risk, which every leader sells, and none of the five covered them.
- Auctions as a module of its own. Rejected: an auction is one way to run a sourcing event, on Sourcing's lots and award; the leaders sell it inside sourcing.
- Surveys as a procurement product. Rejected: surveys need nothing procurement owns; the forms engine already serves supplier information collection and sourcing, and every suite gets surveys from it.
- Projects and tasks as a general project tool. Rejected: it competes with work-management tools a tenant already has; procurement needs category strategies and savings projects tied to its spend.
- Inventory and direct materials now. Deferred: they serve a narrower set of customers and overlap with their ERP systems; the first customers are indirect buyers in regulated industries.
- E-invoicing as country packs only (BF-204). Rejected for the exchange itself: a country's clearance platform, formats and partners are work and cost per country, which a licensed feature lets a tenant buy for the countries it trades in; the packs keep the reference data and field rules (INV-010).
- One buying model per tenant. Rejected: a group often buys centrally for some units or categories and lets people buy for themselves elsewhere.

## Consequences

- Easier: a customer buys the modules it needs, and a country's e-invoicing or auctions without a module of their own; a new module has a scope page, a prefix and a place in the plan before its PRD is written.
- Harder: Requests and Intake carries a buying model with its own resolution order (REQ-121), and Orders gains automatic and consolidated orders (ORD-161 to ORD-164).
- The scope page of a merged or removed module is gone; `requirements-map.md` keeps a note of each change (its section "Scopes changed after the split").
- Revisit: each module's place in the process order when its PRD is written; the deferred modules when a manufacturing-heavy customer asks for them.
