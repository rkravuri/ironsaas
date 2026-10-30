# eProcurement Suite: Category and Savings Projects, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-2 module of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `PRJ-`, reserved; no requirements yet |
| Version | 2.0 (EPS-modules, 2026-10-30: reframed from Projects and tasks, `projects-and-tasks-scope.md`, into category strategies and sourcing and savings projects) |
| Date | 2026-10-30 |

## Purpose

Plan and run procurement's strategic work: category strategies, which say what the tenant buys in a category, from whom, and how it will buy it over the next years; and the sourcing and savings projects that carry a strategy out, each with its tasks, owners, milestones and expected savings. A project here is a procurement initiative. The project as a cost object stays a Business master (BF-075), which a project here may name.

## Users

Category managers, who own a strategy and its projects; project owners and their team members (buyers, people from the business, finance); the approvers of strategies and milestones; and suppliers, who work tasks assigned to them in the supplier portal.

## Main documents

The category strategy, with its category, its scope (units and legal entities), its spend baseline from Spend Analytics and Savings, its market and supplier analysis, its objectives, its plan of projects and its review date; the project, with its kind (sourcing, savings, supplier consolidation, demand management), its scope, team, dates, expected savings and the records it concerns; its tasks with owners, due dates and links to those records; its milestones, each a gate approved before the next stage; its documents.

## The process

A category strategy is drafted from the category's spend and suppliers, reviewed and approved; its plan creates projects; each project is staffed and planned with its tasks; tasks are worked by people or suppliers; a sourcing project runs its events in Sourcing; a savings project records its expected savings in Spend Analytics and Savings, which tracks them to realisation; milestones are reviewed and approved; the project closes when its tasks are done or cancelled; the strategy is reviewed on its date, against the spend and savings since.

## What it needs from the kernel and the business layer

- Kernel: tasks between parties, suppliers included (TF-097); comments (TF-098); the business calendar for due dates in working days (TF-099); approvals of strategies and milestones (TF-090); notifications (TF-150); files (TF-160); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: categories (BF-042), people (BF-021), business groups (BF-024), projects as cost objects (BF-075).
- The suite's purchasing master (ADR-0043): purchasing groups (PUR-001) and supplier segments (PUR-026).
- Earlier modules (ADR-0042): Spend Analytics and Savings, for a category's spend and the savings it tracks; Sourcing, for the events a sourcing project runs; Contracts, for the category's contracts and their expiry (CON-022); Supplier management, for the category's suppliers.

Its place in the process order, proposed 2026-10-30: last, after Spend Analytics and Savings, since it calls every module above and none calls it. Projects and tasks was placed last on 2026-10-20 for the same reason.

## Out of scope

Project management for work outside procurement; time sheets, resource levelling and billing for project work; scheduling by task dependencies beyond due dates; the savings figures themselves, which Spend Analytics and Savings keeps.

## Open questions

- Is a category strategy a fixed set of sections, or a form template of the forms engine (TF-242) the tenant shapes?
- Which gates does each kind of project have, and who approves each?
- How does a project's expected saving stay one figure with the saving Spend Analytics and Savings tracks: one record there, shown here?
- May suppliers work tasks before the Supplier Network exists, through one-time verified access (TF-246)?

Reframed 2026-10-30 (EPS-modules, ADR-0058), from Projects and tasks (version 1.1 of 2026-10-20), which planned any procurement work as projects. Its open question whether it served procurement projects only is answered: it does, and the project as a cost object stays BF-075.
