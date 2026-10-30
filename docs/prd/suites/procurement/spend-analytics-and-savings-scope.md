# eProcurement Suite: Spend Analytics and Savings, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-2 module of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `ANL-`, reserved; no requirements yet |
| Version | 1.0 (EPS-modules, 2026-10-30: the owner's module list of 2026-10-29) |
| Date | 2026-10-30 |

## Purpose

Show where the money goes, cleanly classified, and follow every saving from the moment it is found to the moment it shows in what is paid, so that category managers act on facts and the savings reported are savings realised.

## Users

Category managers and buyers, who read the spend of their categories and suppliers; procurement leadership, who follows the savings; finance controllers, who validate realised savings; and the analysts who keep the classification and the supplier names clean.

## Main documents

The spend cube: spend lines from the suite's orders, invoices and payments, and spend loaded from the ERP for what was bought outside the suite, by supplier, category, unit, legal entity and period, in the transaction currency and the reporting currency. The classification: each line's UNSPSC code, set by the tenant's rules, by the AI service as a suggestion a person confirms, or by hand. The supplier families: the many names of one supplier under one parent. Dashboards and saved views. The savings initiative: its baseline, its method (price reduction, cost avoidance, demand reduction), its target, and its stages, identified, approved, contracted, realised, with the finance validation of what was realised.

## The process

Spend is loaded each night from the modules' analytics feeds and from the ERP; lines are mapped to supplier families and classified, the rules first, then suggestions for a person to confirm; the cube is refreshed and the dashboards read it. A saving is identified, often in a project of Category and Savings Projects, with its baseline and target; it is approved; it is contracted when a contract or an award fixes the new price; it is realised as orders and invoices at the new price are counted against the baseline; finance validates the realised figure each period.

## What it needs from the kernel and the business layer

- Kernel: the analytics feed and exports (TF-089, TF-203); jobs for the nightly load (TF-120); imports and connectors for spend from the ERP (TF-205, TF-206); the AI service for classification suggestions, confirmed by a person (TF-208); saved views and the grid (TF-171, TF-203); approvals for the validation of savings (TF-090); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: suppliers (BF-101), categories and their UNSPSC codes (BF-042, BF-205), legal entities and units (BF-001), currencies and rates (BF-070), the fiscal calendar (BF-069), projects as cost objects (BF-075).
- The suite's purchasing master (ADR-0043): spend by purchasing group (PUR-001) and by supplier segment (PUR-026).
- Earlier modules (ADR-0042): Orders, Receiving and Invoicing, read through their analytics feeds; Contracts, for spend on and off contract (CON-041); Sourcing, for the price an award fixes; Payments, for spend paid.

Its place in the process order, proposed 2026-10-30: after Payments, since it reads the spend of every module before it and none of them calls it. Category and Savings Projects comes after it and calls it.

## Out of scope

The spend of another suite; a data warehouse for the tenant's other data; external price benchmarks and market indices (a later feature); the supplier performance scores, which are Supplier management's (SUP-062).

## Open questions

- At which UNSPSC level does classification stop by default: segment, family, class or commodity?
- Where does spend outside the suite come from first: a file from the ERP, or a connector?
- Which savings methods does the tenant get, and may it define its own?
- How is a saving realised for services and for cost avoidance, where no unit price falls?
- How far back does the first load go, and how long is the cube kept?
