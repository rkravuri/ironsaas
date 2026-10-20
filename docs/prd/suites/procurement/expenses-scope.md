# eProcurement Suite: Expenses, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later product (ADR-0041). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a later product of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `EXP-`, reserved; no requirements yet |
| Version | 1.1 (accepted at the review of 2026-10-20; moved to `docs/prd/suites/procurement/` by ADR-0043) |
| Date | 2026-10-20 |

## Purpose

Let employees claim business expenses, such as travel, meals and mileage, with their receipts, have them checked against policy and approved within the delegation of authority, and reimbursed through the ERP.

## Users

The employee who claims, the manager and the approvers, the finance team who audit claims, and the ERP that reimburses and reports payment.

## Main documents

The expense report with its lines (category, amount, currency, receipt image, project or account string); the policy rules; cash advances; the reimbursement status.

## The process

An expense is captured on the web or in the mobile companion with a photo of its receipt; the report is submitted; policy checks run; the manager chain and the delegation of authority approve it; finance audits it; the ERP reimburses and reports the payment status.

## What it needs from the kernel and the business layer

- Kernel: the mobile companion for end-user expense entry (TF-224, decision D-08); files for receipt images (TF-160); suggestions read from receipts by the AI service, confirmed by the user (TF-208); approvals and resolvers (TF-090, TF-091); notifications (TF-150); access in what is sent (TF-243).
- Business: people and managers (BF-021, BF-023), the delegation of authority (BF-142, BF-143), account strings and budgets (BF-062, BF-065), projects (BF-075), currencies and rates (BF-070), tax codes (BF-071), business lookups for expense categories (BF-121).
- Earlier products (ADR-0042): none required.

Its place in the process order, decided 2026-10-20: it calls no procure-to-pay product, so it stands apart from their order.

## Out of scope

Travel booking; payment execution, which is the ERP's.

## Open questions

- Corporate card feeds in the first version?
- Per diem and mileage rates per country: a country pack (BF-204) or the tenant's own lists?
