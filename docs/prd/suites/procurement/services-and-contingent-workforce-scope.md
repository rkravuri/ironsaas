# eProcurement Suite: Services and Contingent Workforce, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-3 module of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `SVC-`, reserved; no requirements yet |
| Version | 1.0 (EPS-modules, 2026-10-30: the owner's module list of 2026-10-29) |
| Date | 2026-10-30 |

## Purpose

Buy services and contract workers under control: statements of work with their deliverables, milestones and fees; rate cards that set what each role may cost; timesheets that become service receipts; and the onboarding and offboarding of every worker, so that nobody works without an engagement and nothing is paid that was not approved.

## Users

Hiring managers and the owners of a statement of work; buyers; suppliers, service providers and staffing agencies; the contract workers, who submit their timesheets; the approvers of engagements and timesheets; and the people who onboard a worker (identity, background check, access, equipment) and offboard them.

## Main documents

The statement of work, with its scope, deliverables, milestones and fees (fixed, by milestone, or time and materials); the rate card per supplier, role, level and location; the request for a contract worker, with the candidates suppliers submit; the engagement, with its worker, role, rate, dates and cost object; the timesheet, with its hours and expense lines; the onboarding and offboarding checklists.

## The process

A services or contractor need arrives through intake (REQ-101, its types services and contractors) and is approved; the buyer engages a supplier through a statement of work, from a contract or an award, or asks staffing suppliers for candidates within the rate card; the chosen worker is onboarded; timesheets, or milestone confirmations, are submitted and approved, each becoming a service entry against its order line in Receiving (RCV-042, RCV-043); the supplier invoices against them (INV-003); the engagement ends with offboarding, its access removed on its last day.

## What it needs from the kernel and the business layer

- Kernel: tasks between parties for onboarding (TF-097); the forms engine for checklists and candidate profiles (TF-242); approvals (TF-090); the business calendar (TF-099); the Supplier Network and one-time verified access (TF-230 to TF-236, TF-246); files (TF-160); the mobile companion for timesheets (TF-224); notifications (TF-150); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: people (BF-021), units and legal entities (BF-001), projects as cost objects (BF-075), locations (BF-081), currencies and rates (BF-070), suppliers (BF-101).
- The suite's purchasing master (ADR-0043): purchasing groups (PUR-001).
- Earlier modules (ADR-0042), on Receiving's service receipts above all: Receiving's service entry, milestones, percentage of completion and acceptance (RCV-041 to RCV-046); Contracts, for rate cards and statements of work under a contract (CON-002); Requests and Intake, for the needs it fills (REQ-101); Orders, for the order behind each engagement (ORD-001).

Its place in the process order, proposed 2026-10-30: between Receiving and Invoicing, since it calls Contracts, Requests and Intake, Orders and Receiving, and Invoicing invoices the service entries it creates as it does any other.

## Out of scope

Payroll for contract workers; acting as their employer of record; recruiting employees, which is an HR system's; advice on whether a worker is self-employed, which is the tenant's legal team's.

## Open questions

- Is a contract worker a person of the people master with a type of their own, or a record of this module with a sign-in for timesheets?
- Do workers enter timesheets themselves in the tenant, or does their agency enter them in the supplier portal?
- Which rate card dimensions in the first version?
- Which limits per country, such as the longest engagement before co-employment rules apply?
