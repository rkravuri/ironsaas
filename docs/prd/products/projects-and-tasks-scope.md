# Foundation: Projects and tasks, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later product (ADR-0041). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Products (ADR-0029) |
| Identifier prefix | `PRJ-`, reserved; no requirements yet |
| Version | 0.2 DRAFT for review (the split of 2026-10-20) |
| Date | 2026-10-20 |

## Purpose

Plan and follow procurement work as projects, such as a sourcing programme, a wave of supplier onboarding or a category plan, with tasks, owners, milestones and documents. A project as a product is not the project as a cost object, which is a Business master (BF-075); a project here may name one.

## Users

The project owner, the team members who work its tasks, the approvers of its milestones, and suppliers, who work tasks assigned to them in the supplier portal.

## Main documents

The project with its scope, dates, team and cost object; its tasks with owners, due dates and links to the records they concern; its milestones; its documents.

## The process

A project is created with its team; its tasks are planned with owners and due dates; tasks are worked by people or suppliers; milestones are reviewed and approved; the project closes when its tasks are done or cancelled.

## What it needs from the kernel and the business layer

- Kernel: tasks between parties, suppliers included (TF-097); comments (TF-098); the business calendar for due dates in working days (TF-099); approvals of milestones (TF-090); notifications (TF-150); files (TF-160); access in what is sent (TF-243).
- Business: projects as cost objects (BF-075), people (BF-021) and business groups (BF-024).
- Earlier products (ADR-0042): any whose records a task concerns; it calls them to show the record and never the other way.

`[Decision]`, its place in the process order: proposed last, after Invoicing, since its tasks may concern any earlier product's records.

## Out of scope

Time sheets, resource levelling and billing for project work; scheduling by task dependencies beyond due dates.

## Open questions

- Is it for procurement projects only, or for any project the tenant runs?
- Does a project's budget follow its cost object (BF-075), or does it carry one of its own?
