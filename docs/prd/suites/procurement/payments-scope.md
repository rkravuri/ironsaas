# eProcurement Suite: Payments, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later module (ADR-0041, ADR-0058). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a wave-3 module of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `PAY-`, reserved; no requirements yet |
| Version | 1.0 (EPS-modules, 2026-10-30: the owner's module list of 2026-10-29) |
| Date | 2026-10-30 |

## Purpose

Pay suppliers from the suite: turn invoices approved for payment into payment files the tenant's banks accept, tell each supplier what was paid and for which invoices, take early-payment discounts that are worth taking, and offer suppliers early payment financed by partners. The suite never holds money: payments leave the tenant's own bank accounts, and a tenant that pays from its ERP keeps doing so (the Business PRD's section 2.3).

The messages are ISO 20022's: the credit transfer initiation (pain.001) to the bank, the payment status report (pain.002) and the account statements and notifications (camt.053, camt.054) from it, and the remittance advice (remt.001) to the supplier. The payment rails are each country's, reached through the tenant's banks: SEPA Credit Transfer and SEPA Instant in the euro area, Faster Payments and Bacs in the United Kingdom, ACH and Fedwire in the United States, NEFT, RTGS and IMPS in India, and cross-border payments over SWIFT in ISO 20022.

## Users

Accounts payable and treasury, who propose and send payment runs; the approvers of a run; suppliers, who receive remittance advice and early-payment offers; the banks and supplier finance partners the tenant contracts with.

## Main documents

The payment run, with its proposal, approval, files, sending and status; its payment lines, each for one or more invoices of one supplier; the payment file per bank and rail; the bank's status reports and statements; the remittance advice; the early-payment offers, at a fixed discount or one that falls with each day earlier; the supplier finance offers through a partner, who pays the supplier early and is paid by the tenant at the due date.

## The process

Invoices approved for payment (INV-043) are proposed in a run by their due dates and discount dates; each supplier's bank account is checked before release (INV-046, SUP-164); the run is approved by two people with signatures; the files are made per bank and rail and sent through the bank's connection; the status reports update each payment, and the invoice's payment status follows (INV-043, INV-045); the remittance advice reaches the supplier; a supplier may accept an early payment for a discount, or take finance from a partner, before the run that would pay it.

## What it needs from the kernel and the business layer

- Kernel: connectors to banks and partners (TF-114, TF-206) with their keys and secrets in the key service (TF-073 to TF-075); approvals of a run (TF-090); electronic signatures and step-up for its release (TF-063, TF-248); a second approval of sensitive configuration, such as a bank connection (TF-068); jobs (TF-120); notifications (TF-150); the Supplier Network's inbox for offers (TF-233); access in what is sent (TF-243).
- Business: legal entities (BF-001), supplier bank accounts (BF-105), suppliers (BF-101), currencies and rates (BF-070), payment terms and their discounts (BF-072).
- Earlier modules (ADR-0042): Supplier management, whether an account may be paid on a date (SUP-164); Invoicing, the invoices approved for payment, their payment status and its ISO 20022 codes, and the bank account check at release (INV-043, INV-045, INV-046).

Its place in the process order, proposed 2026-10-30: after Invoicing, which it calls; Invoicing hears of payments from its events, as it hears of payment status from the ERP today.

## Out of scope

Holding funds or acting as a payment institution; collecting money from customers; card issuing (virtual cards through a partner may come later); treasury beyond paying suppliers (cash positions, hedging).

## Open questions

- Which banks and rails first, and through which connection: host-to-host, EBICS, or SWIFT?
- What does a tenant that pays from its ERP use: remittance advice and discounts only?
- Is early payment funded by the tenant's cash, by partners, or both, and who carries the partner's checks of the supplier?
- Who may approve a run, and is the second approval always required?
