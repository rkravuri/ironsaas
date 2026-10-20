# Foundation: Auctions, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later product (ADR-0041). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Products (ADR-0029) |
| Identifier prefix | `AUC-`, reserved; no requirements yet |
| Version | 0.2 DRAFT for review (the split of 2026-10-20) |
| Date | 2026-10-20 |

## Purpose

Run live reverse auctions in which invited suppliers bid prices down in real time for lots, so that a competitive price is found in an hour rather than over rounds of quotations.

## Users

The buyer who owns the auction, observers who follow it, the approvers of its rules and result, and suppliers, who bid in the supplier portal.

## Main documents

The auction with its lots, its window and its rules (what bidders see of the ranking, the minimum step between bids, the extension when a bid arrives late); the bids; the result handed to the award.

## The process

Lots are drafted, alone or from a sourcing event; the rules are set and approved; suppliers are invited through the Supplier Network and may place bids before it opens; during the live window each bid updates the ranking at once; a late bid extends the window; the auction closes and its result goes to the award.

## What it needs from the kernel and the business layer

- Kernel: live push to every bidder (TF-153), on a shared backplane when the unit runs several nodes (TF-225), with its kill switch (TF-051); times shown with their zone (TF-142); feature entitlements, such as pre-bidding (TF-046); the Supplier Network (TF-230 to TF-236); an audit event for every bid (TF-060); access in what is sent (TF-243).
- Business: suppliers and their enablement (BF-101, BF-108), items (BF-041), currencies (BF-070).
- Earlier products (ADR-0042): Sourcing, whose lots it auctions and whose award takes its result; Supplier management, for invited suppliers.

`[Decision]`, its place in the process order: proposed right after Sourcing.

## Out of scope

Forward auctions, in which the tenant sells; auctions open to suppliers the tenant has not invited.

## Open questions

- Which formats in the first version: the reverse auction with a visible ranking only, or also sealed bids and best price shown?
- How are ties broken, and how long is an extension?
- What happens to a bid when the network loses a bidder's connection (TF-238)?
