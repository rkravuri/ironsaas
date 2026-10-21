# SUP-gaps: Supplier management, every gap closed, into the requirements

Written on 2026-10-21 in the design session (ADR-0023). The process reference pack for Supplier management (version 0.2) was reviewed by the owner: all thirteen gaps are accepted, and two areas are designed apart: supplier information change management, and periodic supplier information collection (SIM) on the forms engine. This task puts the pack into the repository, draws its maps as BPMN files, and writes every accepted decision into the requirement documents. Documentation only.

## Opening message

```
Read CLAUDE.md, docs/STATUS.md, docs/adr/0041, 0042 and 0043, docs/prd/README.md, and in
docs/prd: the supplier management, purchasing, orders, receiving, invoicing and contracts PRDs, the
supplier portal PRD, BF-101 to BF-108, BF-141, BF-204 and BF-207 in the Business Foundation PRD,
and TF-036, TF-046, TF-062, TF-063, TF-196 to TF-198, TF-206, TF-219 and TF-242 in the technical
PRD. Then read docs/prompts/SUP-gaps.md and the pack it names, and follow it: branch
slice/sup-gaps from main, commit the prompt file and the pack alone first, write a short plan into
the session, and work without waiting for approval unless the "When to stop" rule in CLAUDE.md
applies. Done when: the pack is in the repository with its maps as BPMN files; every decision of
the pack's section 8 is a requirement with acceptance criteria in the PRD it names; the replaced
requirements say what replaced them; the pack's section 2 maps every new requirement to a PCF
element and a map step; the technical PRD's section 7.2 places the new requirements in slices;
the domain models follow the pack's section 8.5; make prd-check passes; make test, make shell-check and make format are clean. Finish with the
report: the new and changed requirements per document, the retired ones with their successors,
and every rule you had to choose that the pack does not state. Then stop.
```

## Inputs

- The pack: save `supplier-management-pack.md` from the design session as `docs/prd/suites/procurement/process-reference/supplier-management.md`. Its section 8 is the specification for this task.

## Steps

1. **The prompt commit**, with the pack in its place.
2. **BPMN files.** Draw the maps of sections 3.1, 3.3, 3.4 and 3.5 as BPMN 2.0 XML files in `docs/prd/suites/procurement/process-reference/supplier-management/`, one per map: lanes for the roles, tasks with the step identifiers as their names, gateways where the maps branch. Each file must be well-formed and valid against the BPMN 2.0 schema; add that check to `make prd-check`. The Mermaid maps stay in the pack for reading on GitHub.
3. **Supplier management PRD**, new areas and rewritten requirements, each with acceptance criteria in the usual format, including an error case:
   - Supplier requests (G1).
   - Risk tiers and tiered requirements (G12), extending SUP-005.
   - Verification and screening: identifier re-verification (G4), automated bank verification with the call-back and the cooling period (G5), sanctions screening (G6).
   - Qualification: the qualification matrix by scope (G2); regulated qualification (G3), declared as the licensed feature `supplier_management.gxp_qualification` (TF-046), with the quality unit's electronic signature (TF-063).
   - **Supplier information change management** (section 8.2): the change request and its sources, the three field classes with their defaults, per-field decisions, routing, segregation, effective dates and versions, concurrency, one pending request per field, the impact preview, notifications (including the announcement to the contacts known before a bank change), signatures, and measures. SUP-022 and SUP-023 are replaced by this area.
   - **Periodic supplier information collection, SIM** (section 8.3): templates on the forms engine, single requests and campaigns with their targets, schedules and triggers, reminders, escalation and non-response rules, the review per section, the automatic application through change management, documents with expiry, outcomes, and tracking. SUP-042 and SUP-102 are replaced by this area.
   - Offboarding (G8), extending SUP-021; contact data protection (G10); onboarding measures with step timestamps (G11), extending SUP-063.
   - A replaced requirement keeps its identifier, is marked retired with "replaced by" and its successors, and is never reused (ADR-0041).
4. **Domain models.** Apply the pack's section 8.5 to the domain model section of every PRD this task touches: the supplier management PRD (change requests as the kernel's, information requests and campaigns, qualifications with their scope kinds, risk tiers, screening and verification results), the purchasing PRD (segments), the Business PRD (supplier sites with purposes), and the technical PRD (governed changes, form responses and their mappings). Entities, relationships and rules only; no tables or columns.
5. **Other documents.**
   - Purchasing PRD: segmentation (G9); PUR-022's preferred status only for qualified scopes (G2).
   - Orders PRD: at submission, an order line for a quality-relevant item only to an approved supplier, site and material (G3).
   - Contracts PRD: a quality agreement linked to a regulated qualification (G3).
   - Invoicing PRD: the cooling period and the re-check at payment release (G5); for India, the cap on payment terms for micro and small suppliers, the ageing report by 15 and 45 days, and the data for MSME Form 1 (G7).
   - Receiving PRD: the acceptance date that starts the MSME clock (G7).
   - Business Foundation PRD: BF-204's India pack names the Udyam classification, the GSTIN and PAN status checks and the MSME rules (G4, G7). Keep risk tier and segment out of Business (ADR-0043 decision 8): they are the suite's.
   - Supplier portal PRD: information requests in the portal, the one-time verified link (G13), and the supplier's view of its change requests with the outcome of each field.
6. **Kernel requirements** in the technical PRD (section 8.4 of the pack): TF-247 Governed changes to records (new, in S5 after the workflow engine; supplier change management is built on it); TF-242 extended (attachments with issue and expiry dates, section reviewers and per-section decisions, pre-fill, versions of responses with comparison); TF-245 Mapping form answers to records (new; a mapped answer is a change proposal handed to the module that owns the record, never a direct write); TF-246 One-time verified access for external respondents (new); TF-206 naming the connector kinds for identity and tax status, bank accounts and sanctions. Section 7.1 places TF-245 and TF-246 in S4 with the forms engine.
7. **Slices.** Section 7.2 places the new supplier requirements. Note there that TF-246 lets suppliers answer information requests and invitations before the Supplier Network exists (S8), so SIM can ship with Supplier management (PR1) and move into the portal with PR8.
8. **The pack's section 2** gains a row for every new requirement, with its PCF element and map step.
9. `make prd-check`, `make test`, `make shell-check` and `make format`; STATUS; the report.

## Rules for the writing

The pack's section 8 is the source. Where it does not state a rule that a requirement needs (a default, a limit, an order of checks), write the requirement with a proposed default and tag it `[Decision]`, with the question in its notes; list every one in the report. Plain English, short sentences, no em dash.

## Review checklist

- [ ] Every gap G1 to G13 and both designed areas are requirements, each with an error case.
- [ ] Change management is the only way a change reaches an approved supplier, and SIM applies through it.
- [ ] Governed changes are the kernel's (TF-247); the supplier-specific parts are hooks.
- [ ] The domain models follow the pack's section 8.5, without tables or columns.
- [ ] A critical field from any source is verified before it applies.
- [ ] SUP-022, SUP-023, SUP-042 and SUP-102 are retired with their successors named.
- [ ] TF-245 and TF-246 are in S4; the BPMN files are valid.
