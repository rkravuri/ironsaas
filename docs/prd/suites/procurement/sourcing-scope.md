# eProcurement Suite: Sourcing (RFx), scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-2 module of the eProcurement Suite, on IronSaaS Foundation, with auctions as a licensed feature, `sourcing.auctions` (TF-046) |
| Identifier prefix | `SRC-`, reserved; no requirements yet. Auctions' requirements take `SRC-` too: `AUC-` stays reserved and is never used (ADR-0058) |
| Version | 1.2 (EPS-modules, 2026-10-30: Auctions merged in as a licensed feature, bid comparison and award scenarios named, wave 2) |
| Date | 2026-10-30 |

## Purpose

Run requests for information, proposals and quotations (RFI, RFP, RFQ) with suppliers, compare their bids, model award scenarios and award the business, so that contracts and orders follow from a competitive event rather than from one supplier's price. As a licensed feature, run live reverse auctions in which invited suppliers bid prices down in real time for lots, so that a competitive price is found in an hour rather than over rounds of quotations.

## Users

The buyer who leads the event, the evaluators who score responses, the approvers of the event and the award, the requester whose need started it, observers who follow an auction, and suppliers, who respond and bid in the supplier portal.

## Main documents

The sourcing event with its lines (items or services), its questions and its evaluation criteria; the invitations to suppliers; each supplier's response with prices and answers; the bid comparison, line by line and in total; the award scenarios (lowest price per line, one supplier for all, a split by share, with constraints such as a maximum number of suppliers), each with its total; the evaluation with its scores; the award. With the auctions feature: the auction with its lots, its window and its rules (what bidders see of the ranking, the minimum step between bids, the extension when a bid arrives late), and its bids, whose result goes to the award.

## The process

A need, from a request line routed to sourcing first (REQ-141) or from a buyer, becomes an event with lines, questions and criteria; the event is approved; suppliers are invited through the Supplier Network; they respond in the portal by the deadline; the evaluators score the responses; the buyer compares the bids and models award scenarios; the award is approved; a contract or orders follow from it. An auction runs inside an event: its lots are drafted from the event's lines; the rules are set and approved; suppliers are invited and may place bids before it opens; during the live window each bid updates the ranking at once; a late bid extends the window; the auction closes and its result goes to the award.

## What it needs from the kernel and the business layer

- Kernel: the forms engine for questions, scoring and weights (TF-242); approvals (TF-090, TF-091); the Supplier Network's invitations, inbox and delegated access (TF-230 to TF-236), and one-time verified access for suppliers before it (TF-246); notifications (TF-150); files (TF-160); numbering (TF-166); signatures on the award (TF-063); feature entitlements for auctions and pre-bidding (TF-046); for auctions, live push to every bidder (TF-153), on a shared backplane when the unit runs several nodes (TF-225), with its kill switch (TF-051), and times shown with their zone (TF-142); an audit event for every bid (TF-060); visibility tags (TF-241) and access in what is sent (TF-243).
- Business: suppliers and their enablement (BF-101, BF-108), items and categories (BF-041, BF-042), currencies and rates (BF-070), people (BF-021) and the delegation of authority (BF-142, BF-143).
- The suite's purchasing master (ADR-0043): purchasing groups (PUR-001) and the suppliers' purchasing profiles (PUR-021, PUR-022).
- Earlier modules (ADR-0042): Supplier management, so that only approved suppliers are invited (SUP-004); Supplier Risk and Sustainability, for a supplier's risk when invited and at award; Contracts, which an award creates; Requests and Intake, whose lines routed to sourcing it sources (REQ-141).

Its place in the process order, decided 2026-10-20: between Requests and Orders, since it reads request lines and creates contracts, and Orders creates orders from an award. Auctions, a feature of this module since 2026-10-29, take its place.

## Out of scope

Contract authoring and negotiation (Contracts' authoring tier, CON-101, wave 2); supplier risk scoring from external data (Supplier Risk and Sustainability); forward auctions, in which the tenant sells; auctions open to suppliers the tenant has not invited; bill-of-materials sourcing for direct materials (deferred, ADR-0058).

## Open questions

- Are responses sealed until the deadline, and who may open them?
- May an award create orders directly, or only through a request?
- Several rounds in one event, or a new event per round?
- Responses in the supplier's currency, converted at which date's rate?
- Which award constraints in the first version, and is an optimiser needed for them?
- Which auction formats in the first version: the reverse auction with a visible ranking only, or also sealed bids and best price shown?
- How are an auction's ties broken, and how long is an extension?
- What happens to a bid when the network loses a bidder's connection (TF-238)?

Merged 2026-10-30 (EPS-modules, ADR-0058): `auctions-scope.md` (version 1.1 of 2026-10-20), whose purpose, documents, process, needs, out of scope and open questions are above.
