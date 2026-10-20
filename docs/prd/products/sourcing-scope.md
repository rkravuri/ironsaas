# Foundation: Sourcing (RFx), scope

| Item | Value |
|---|---|
| Document | One-page scope of a later product (ADR-0041). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Products (ADR-0029) |
| Identifier prefix | `SRC-`, reserved; no requirements yet |
| Version | 0.2 DRAFT for review (the split of 2026-10-20) |
| Date | 2026-10-20 |

## Purpose

Run requests for information, proposals and quotations (RFI, RFP, RFQ) with suppliers, evaluate their responses and award the business, so that contracts and orders follow from a competitive event rather than from one supplier's price.

## Users

The buyer who leads the event, the evaluators who score responses, the approvers of the event and the award, the requester whose need started it, and suppliers, who respond in the supplier portal.

## Main documents

The sourcing event with its lines (items or services), its questions and its evaluation criteria; the invitations to suppliers; each supplier's response with prices and answers; the evaluation with its scores; the award.

## The process

A need, from a request line without a supplier or from a buyer, becomes an event with lines, questions and criteria; the event is approved; suppliers are invited through the Supplier Network; they respond in the portal by the deadline; the evaluators score the responses; the award is approved; a contract or orders follow from it.

## What it needs from the kernel and the business layer

- Kernel: the forms engine for questions, scoring and weights (TF-242); approvals (TF-090, TF-091); the Supplier Network's invitations, inbox and delegated access (TF-230 to TF-236); notifications (TF-150); files (TF-160); numbering (TF-166); signatures on the award (TF-063); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: suppliers and their enablement (BF-101, BF-108), items and categories (BF-041, BF-042), currencies and rates (BF-070), purchasing groups (BF-007), people (BF-021) and the delegation of authority (BF-142, BF-143).
- Earlier products (ADR-0042): Supplier management, so that only approved suppliers are invited (SUP-004); Contracts, which an award creates; Requests, whose lines it sources.

`[Decision]`, its place in the process order: proposed between Requests and Orders, since it reads request lines and creates contracts, and Orders creates orders from an award.

## Out of scope

Auctions (their own product, `auctions-scope.md`); contract authoring and negotiation (later scope of Contracts); supplier risk scoring from external data.

## Open questions

- Are responses sealed until the deadline, and who may open them?
- May an award create orders directly, or only through a request?
- Several rounds in one event, or a new event per round?
- Responses in the supplier's currency, converted at which date's rate?
