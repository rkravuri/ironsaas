# eProcurement Suite: Supplier Risk and Sustainability, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-2 module of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `RSK-`, reserved; no requirements yet |
| Version | 1.0 (EPS-modules, 2026-10-30: the owner's module list of 2026-10-29) |
| Date | 2026-10-30 |

## Purpose

Watch every supplier's risk continuously, and meet the due-diligence laws that make a buyer answerable for its supply chain: ask each law's questions of the suppliers it reaches, keep the evidence, and follow every finding to a corrective action that closes it. Beside risk, keep what the tenant reports about its suppliers: their diversity status and their emissions data.

The laws named at the owner's decision, with their dates as read on 2026-10-29:

- The EU Deforestation Regulation (Regulation (EU) 2023/1115 as amended), applying from 30 December 2026: due diligence on the commodities it names and their products, with the geolocation of the plots of land they come from, and a due-diligence statement before they are placed on the EU market.
- The EU forced labour regulation (Regulation (EU) 2024/3015), applying from December 2027: products made with forced labour may not be placed on the EU market or exported from it, so a buyer must know who makes what it buys.
- The Corporate Sustainability Due Diligence Directive (Directive (EU) 2024/1760 as amended), for the first companies from July 2028: identifying, preventing and ending harm to people and the environment in the chain of activities, with corrective action plans.

Other laws of the same kind, a country's own supply chain act among them, join the same way: as a questionnaire set and a report per law.

## Users

Risk and compliance managers, who own the monitoring and the findings; the sustainability team, who collects emissions and diversity data; buyers and category managers, who see a supplier's risk where they choose suppliers; suppliers, who answer questionnaires, upload evidence and carry out corrective actions; auditors, who read the evidence per law.

## Main documents

The supplier's risk profile, with a score per dimension (financial, sanctions, cyber, environmental and social, country, operational), each from external feeds, answers and findings, and its history; the alerts monitoring raises; the due-diligence questionnaire sets per law, sent through Supplier management's information collection; the evidence (certificates, geolocation data, audit reports); the findings and their corrective action plans, with owners, due dates and proof of closure; the diversity certificates; the emissions data per supplier (its own figures where it gives them, or factors by spend); the reports per law.

## The process

Suppliers in scope of a law are selected by category, country and segment; feeds monitor them continuously and an alert opens a review; due-diligence campaigns per law go out through Supplier management's information collection, answered in the portal or through one-time verified access; answers and evidence are reviewed section by section; a finding becomes a corrective action plan agreed with the supplier and followed to closure, or to the end of the relationship; the supplier's risk tier is proposed to Supplier management, which decides it; the reports per law are produced for the period.

## What it needs from the kernel and the business layer

- Kernel: the forms engine with answers mapped to records and one-time verified access (TF-242, TF-245, TF-246); connectors for risk, sanctions and sustainability data providers (TF-206); jobs for monitoring (TF-120); tasks between parties for corrective actions (TF-097); notifications (TF-150); files (TF-160); signatures on findings and closures (TF-063); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: suppliers, their sites and categories (BF-101, BF-104, BF-107), country packs (BF-204).
- The suite's purchasing master (ADR-0043): supplier segments (PUR-026).
- Earlier modules (ADR-0042): Supplier management, on which it builds: periodic supplier information collection, SIM (SUP-221 to SUP-231), sanctions and watch-list screening and re-verification (SUP-161, SUP-165), the risk tier and risk flags (SUP-007, SUP-043), and audits and findings (SUP-184).

Its place in the process order, proposed 2026-10-30: right after Supplier management, which it calls; Contracts, Sourcing and Orders, later, ask it for a supplier's risk.

## Out of scope

The tenant's own carbon accounting (its scopes 1 and 2) and its sustainability reporting beyond the supplier data it needs; being a source of risk data, which providers are; legal advice on whether a law applies to the tenant.

## Open questions

- Which risk and sustainability data providers first, and for which countries?
- Does the suite submit the EU Deforestation Regulation's due-diligence statements to the EU's information system, or does the tenant's operator?
- Which laws' questionnaire sets ship as the suite's content, and which does the tenant build?
- Emissions: spend-based factors only at first, or the supplier's own figures from the start?
- How does a corrective action reach a supplier without a portal account (TF-246)?
