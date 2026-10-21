# eProcurement Suite: Surveys, scope

| Item | Value |
|---|---|
| Document | One-page scope of a later product (ADR-0041). Its full PRD starts from its own process reference pack (PM-001), reviewed by an external practitioner (PM-002). |
| Layer | Suite (ADR-0043): a later product of the eProcurement Suite, on IronSaaS Foundation |
| Identifier prefix | `SRV-`, reserved; no requirements yet |
| Version | 1.1 (accepted at the review of 2026-10-20; moved to `docs/prd/suites/procurement/` by ADR-0043) |
| Date | 2026-10-20 |

## Purpose

Send surveys to people of the tenant or to suppliers, such as satisfaction surveys, supplier self-assessments or compliance attestations, and analyse their answers. A supplier questionnaire for qualification is Supplier management's, as periodic supplier information collection (SUP-221 to SUP-231, which replaced SUP-042 on 2026-10-21); both stand on the same forms engine.

## Users

The survey's owner, its respondents (people of the tenant, supplier contacts), and the analysts who read the results.

## Main documents

The survey, a form template with its audience and its window; the invitations; the responses; the results.

## The process

A survey is designed on a form template; its audience is chosen (people, business groups, suppliers of a category); it is sent with a window and reminders; responses arrive, named or anonymous; the results are analysed and exported.

## What it needs from the kernel and the business layer

- Kernel: the forms engine (TF-242); notifications with reminders and digests (TF-150, TF-151); the Supplier Network's inbox for supplier respondents (TF-233); exports (TF-203); privacy for anonymous responses and personal data (TF-196); access in what is sent (TF-243).
- Business: people (BF-021), business groups (BF-024), suppliers and their categories (BF-101, BF-107).
- Earlier products (ADR-0042): none required.

Its place in the process order, decided 2026-10-20: it calls no product, so it can sit first; a product that wants a survey's results hears of them through its events.

## Out of scope

Market research panels; surveys open to anyone outside the tenant and its suppliers.

## Open questions

- What does "anonymous" promise: no identity stored, or identity hidden from the owner?
- When is a supplier survey a questionnaire of Supplier management instead?
