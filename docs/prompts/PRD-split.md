# The PRD split: one requirements document per layer and product

Written on 2026-10-20 in the design session (ADR-0023). ADR-0029 placed every piece of Foundation in one of three layers: kernel, business, products. The requirements still live in two documents: the Technical Foundation PRD (already kernel in substance) and the Functional Foundation PRD for procure-to-pay, which mixes shared business masters with products. This task gives every layer and product its own document, completes what is missing, and keeps a map so that no requirement is lost. It is documentation only, plus one small check script.

## Opening message

```
Read CLAUDE.md, docs/STATUS.md, docs/adr/0029-three-layers-kernel-business-products.md and
docs/adr/0023, then docs/prd/technical-foundation-PRD.md and docs/prd/functional-foundation-p2p-PRD.md
in full, with their requirement CSVs. Then read docs/prompts/PRD-split.md and follow it: branch
slice/prd-split from main, commit the prompt file alone first, write a short plan into the session,
and work without waiting for approval until the checkpoint after step 4, where you stop and report.
After the design session answers, continue from step 5. Done when: every requirement of the two
PRDs has a home in exactly one document and a row in the map; the Business PRD and the product PRDs
exist with stable identifiers; the technical PRD has the new kernel requirements and an up-to-date
module table; the five new products have one-page scopes; make prd-check passes and CI runs it;
make test, make shell-check and make format are clean. Finish with the report, the counts per
document, and the list of open points for the design session. Then stop.
```

## Decisions already taken (do not reopen them; record them in ADR-0041)

1. **One document per layer and product**, in `docs/prd/`:

   | Document | File | Identifier prefix |
   |---|---|---|
   | Technical Foundation PRD (the kernel) | `technical-foundation-PRD.md` (kept) | `TF-` (kept) |
   | Business Foundation PRD | `business-foundation-PRD.md` | `BF-` |
   | Supplier management | `products/supplier-management-PRD.md` | `SUP-` |
   | Catalogue | `products/catalogue-PRD.md` | `CAT-` |
   | Purchase requests | `products/requests-PRD.md` | `REQ-` |
   | Purchase orders | `products/orders-PRD.md` | `ORD-` |
   | Receiving (shipment notices, goods and service receipts) | `products/receiving-PRD.md` | `RCV-` |
   | Invoicing | `products/invoicing-PRD.md` | `INV-` |
   | Contracts | `products/contracts-PRD.md` | `CON-` |
   | Supplier portal and Supplier Network experience (a surface) | `surfaces/supplier-portal-PRD.md` | `SPX-` |
   | Product method (binding for every business and product document) | `product-method.md` | `PM-` |
   | Sourcing (RFx), Auctions, Projects and tasks, Surveys, Expenses | `products/<name>-scope.md`, one page each | `SRC-`, `AUC-`, `PRJ-`, `SRV-`, `EXP-` reserved |

   Each document has a CSV next to it with the same columns as today (`id`, `area`, `title`, `priority`, `tag`, `requirement`, `rationale`, `acceptance_criteria`, `depends_on`, `notes`) plus `was`, the old identifier when there is one. Identifiers are stable, never reused, and numbered in blocks of twenty per area, so areas can grow.
2. **New identifiers everywhere outside the kernel.** Every FF requirement gets a new identifier in its new home, and keeps its old one in the `was` column and in its notes ("was FF-022"). The functional PRD moves to `docs/prd/archive/functional-foundation-p2p-PRD-v1.md`, with a first line saying it is superseded and pointing to the map.
3. **The map.** `docs/prd/requirements-map.md` and its CSV list every FF identifier, its new identifier or identifiers, its new document, and what happened to it (moved, split, merged, covered by a kernel requirement, or retired with a reason). A requirement covered by the kernel names the TF requirement and the S2 task that built it.
4. **One acceptance-criteria format** in every document: `AC1 Given ..., When ..., Then ...`, at least one error case per requirement, marked `(error case)`. A test can then name what it proves, for example `REQ-012 AC2` (TF-218). The S3 task that tags the tests depends on these identifiers.
5. **The placement test of ADR-0029 decides every case not listed below**: no business meaning is kernel; needed by two or more products is business; owned by one product is that product.

## Where every functional requirement goes

By area, with the exceptions listed:

| Old identifiers | New home | Notes |
|---|---|---|
| FF-001 to FF-015 (finance masters) | Business | FF-010 categories and commodities, FF-013 units of measure and FF-014 payment and shipping terms stay together in Business |
| FF-020 to FF-039 (suppliers) | Split | **Business** holds the supplier record and its parts, which every product reads: FF-022 master, FF-026 addresses, FF-027 contacts (the data; portal users follow SPX), FF-028 sites, FF-029 bank accounts (the data), FF-035 configuration defaults, FF-038 categories and preferred status. **Supplier management** holds the life cycle: FF-020 invitation, FF-021 registration, FF-023 duplicates, FF-024 approval and publishing, FF-025 status control, FF-029's verification, FF-030 documents and compliance expiry, FF-031 questionnaires (on the forms engine), FF-032 performance, FF-033 change approval, FF-034 the 360 view, FF-036 portal ownership, FF-037 merge, FF-039 risk flags |
| FF-040 items | Business | the item master; supplier items stay in Catalogue |
| FF-041 to FF-049 | Catalogue | |
| FF-050 to FF-063 | Requests | except FF-062 ship-to locations, which goes to Business (locations) |
| FF-070 to FF-086 | Orders | |
| FF-090 to FF-104 | Receiving | except FF-100 warehouses and locations, which goes to Business (locations) |
| FF-110 to FF-134 | Invoicing | FF-125 keeps the due-date rules; the payment terms master is Business (FF-014) |
| FF-140 to FF-147 | Contracts | |
| FF-150, FF-158, FF-159 | Supplier portal surface | FF-158's tasks use the kernel's tasks when they exist |
| FF-151 to FF-157 | The product each view belongs to | FF-151 Orders, FF-152 and FF-154 Receiving, FF-153 and FF-157 Supplier management, FF-155 Invoicing, FF-156 Catalogue; each product PRD gets a "Supplier views" section |
| FF-160 to FF-166 (reporting) | Split | each product's operational reports go to that product (FF-160, FF-161 to Orders and Invoicing); FF-162 budget reports to Business; FF-163 supplier reports to Supplier management; FF-164 analytics feed, FF-165 document search and FF-166 exports are covered by kernel requirements (data platform, TF-204 search, TF-205 and TF-206) with each product declaring what it feeds, indexes and exports |
| FF-170 to FF-176 (configuration) | Split | FF-170 document type settings, FF-173 document templates, FF-174 approval starter set, FF-175 go-live checklist and FF-176 country packs to Business; FF-171 to Supplier management; FF-172 to Invoicing |
| FF-180 to FF-189 (organisation separation) | Mostly covered by the kernel | FF-180, FF-181, FF-183, FF-184 and FF-187 are covered by TF-220 to TF-223 as built in S2 (A1, A3, A6, A8): map them as covered, and move any business detail they add to Business. FF-182 supplier enablement per unit and FF-185 finance masters per legal entity go to Business; FF-186 to Catalogue and Contracts; FF-188's checklist items to Business (they are contributions to the kernel's `IUnitReadiness`); FF-189 to the supplier portal surface |
| FF-190 to FF-197 (Supplier Network experience) | Supplier portal surface | it rests on the kernel's Supplier Network plane, TF-230 to TF-239 |
| FF-200, FF-201, FF-203, FF-204, FF-205 | Product method | binding for Business and every product |
| FF-202 reference data from standards | Business | |

## The Business Foundation PRD

Its structure follows the old PRD's (control, vision, scope, personas, domain model, requirements by area, rules and state machines, data and integration, permissions, configuration, slices, decisions and open points, glossary). Its areas, with what is new marked:

1. **Organisations.** Legal entity attributes (FF-001, FF-185); business units and departments as kinds in the kernel's unit tree, supplied through `IOrganisationKinds` (new); **purchasing groups as a business master**, not a unit kind: a group of buyers, the categories it covers and the units it serves (new). The A1 amendment in `docs/prompts/S2.md` listed purchasing group among the unit kinds; the Business PRD corrects that, with the reason (a purchasing group serves several units and owns no records), and STATUS notes it.
2. **People** (new): the people master linked to kernel principals: employee identifier, designation, job grade, manager (the reporting line), business groups (teams of people for routing and notifications, not for visibility and not roles), default unit, default ship-to and default account string. It implements the kernel's `IReviewEscalation` (the reviewer's manager, from A7).
3. **Items and classification:** items (FF-040), categories and commodities (FF-010, with UNSPSC from FF-202), units of measure with conversions (FF-013).
4. **Finance:** chart of accounts and segments, where GL codes are a segment, stated plainly (FF-002); account strings (FF-003); budgets (FF-004 to FF-008); fiscal calendar, financial years and periods (FF-009); currencies and exchange rates (FF-011); tax codes and classifications (FF-012), with **HSN and SAC codes explicit** (new); payment and shipping terms (FF-014); **projects as cost objects** (new: code, validity, owning unit, budget link).
5. **Locations:** ship-to, bill-to, warehouses (FF-062, FF-100).
6. **Supplier master:** as placed above, with enablement per unit (FF-182).
7. **Lookups:** the business lists every product uses, built on the kernel's custom lookups (TF-201): which lists exist, who maintains them, the seeded values (new).
8. **Approvals and delegation of authority** (new): approval configurations per document type on the kernel's workflow engine (TF-090 to TF-099, built in S5), and the **delegation of authority matrix**: approval limits by amount and currency, by designation, job grade or role, by category, per legal entity or unit, with validity dates and audit. Delegation of duties (TF-037, built in A7) is the kernel's; this matrix decides who may approve what.
9. **Numbering, templates and notifications:** number series per document type and unit on the kernel's numbering service (TF-166); document type settings (FF-170); document templates (FF-173); business notification templates and who is told what, on the kernel's notifications (TF-150 to TF-156) (new).
10. **Content visibility on masters:** which masters carry visibility tags (at least suppliers, items, contracts and projects), on the kernel mechanism TF-241.
11. **Setup and data:** approval starter set (FF-174), go-live checklist as `IUnitReadiness` contributions (FF-175), country accelerator packs including India GST (FF-176), reference data from standards (FF-202), masters import, export and ERP sync (FF-015).
12. **Kernel extension points** this layer implements: `IOrganisationKinds`, `IReviewEscalation`, `IUnitReadiness`, `IUnitRecordOwners` (for masters that move in a unit merge).
13. **Modules:** propose the business modules by the placement test (for example `Iron.Business.Organisations`, `People`, `Items`, `Finance`, `Locations`, `Suppliers`, `Lookups`, `Approvals`), each with its schema `business_<module>` and its permissions.

## Every product PRD

Same structure, scaled to its size. Each one has, beyond its requirements:

- **Supplier views:** what the supplier sees and does in the portal for this product.
- **Records and signatures (Part 11):** which records are approved, acknowledged or signed, and therefore need electronic signatures (TF-063, TF-219); which need an audit-trail review; their retention class (TF-066). Refer to the kernel requirements; do not repeat them.
- **Visibility tags:** how its transactions take their tags (for example, an order from its request or its supplier), by TF-241's rule 6.
- **Dependencies:** the business and kernel requirements it needs, by identifier.
- **Its layer, modules, schemas and permissions.**
- **Service receipts** in Receiving are expanded (new): service entry against an order line, milestones, percentage of completion, acceptance by the requester, and partial acceptance.
- **Contracts** states that authoring and negotiation are later scope.

## The five new products (one page each)

`sourcing-scope.md`, `auctions-scope.md`, `projects-and-tasks-scope.md`, `surveys-scope.md`, `expenses-scope.md`. Each page: purpose, users, main documents, the process in a few lines, the kernel and business requirements it will need (for example, Surveys and Sourcing evaluations need the forms engine, Auctions needs live push, Expenses needs the mobile companion TF-224), what is out of scope, and open questions. Each states that its full PRD starts from its own process reference pack (PM, from FF-200). No requirement identifiers yet; the prefixes are reserved.

## The technical PRD (the kernel)

1. **Section 2.1** becomes the kernel's module table as it is today: the modules built in S1 and S2 by their real names (`Iron.Kernel.Identity`, `.Authorization` with the unit tree, scopes, policies, delegation, reviews and conflict rules, `.Audit`, `.Files`, `.Tenancy`, `.Fleet`, `.Entitlements`, `.Flags`, `.Settings`, and sharing in `Iron.Kernel.Data`), then the planned ones (Workflow, Events, Jobs, Localization, Notifications, Extensibility, Search, Integration, Billing, Compliance, Mobile, Network, Scale, Forms, Visibility). The old `Iron.Organisations` row is retired with a note that S2 built it inside Authorization and Data. Business products are not listed here; one line points to the Business PRD and the product PRDs.
2. **TF-241 Content visibility and visibility tags** (new), with these rules:
   1. Administrators define the visibility tags.
   2. Master data and transactions can carry tags; a record without tags is visible to everyone who passes roles and units.
   3. A person can hold tags; a person without tags sees every record.
   4. A person sees a tagged record when they share at least one tag with it.
   5. A person can only put tags they hold on a record.
   6. Transactions may take their tags from their master; each product says how.
   It also states: the filter applies after roles, permissions and units, inside the same query, and only narrows (ADR-0032); tag changes on records and people are audited; the access explanation shows it as a check (ADR-0039); access reviews cover who holds which tags. Acceptance criteria include rule 4 with a record tagged APAC and LATAM seen by a person holding only APAC. It is `[Core]`, and section 7 schedules it **before the first business slice**, because every business record type needs it from its first day. The old name "content groups" appears once, as an alias.
3. **TF-242 Forms engine** (new): form templates with sections, questions of typed answers, validation, conditional questions, scoring and weights, versions; instances attached to a record; responses audited, with an optional electronic signature; used by supplier questionnaires, onboarding and supplier information forms, surveys and sourcing evaluations.
4. **TF-099** (business calendar) and **TF-166** (numbering service) are confirmed as kernel requirements; add a sentence to each saying which business configuration uses them.
5. **TF-218** (validation support) gains one sentence: requirement identifiers of every PRD (TF, BF, the product prefixes) are traced to tests the same way.
6. **Section 5a** gains one sentence: each product PRD lists its signed records, audit-trail reviews and retention classes.
7. **Section 7** (build plan): TF-241 before the first business slice; then the business slices and the product slices in an order that respects each module's kernel dependencies (approvals configuration after the workflow engine, S5; notification templates after notifications, S4; supplier questionnaires after the forms engine). Propose the order; the design session confirms it.
8. The change log and the document control say what changed and why.

## The check

`make prd-check` (a small script, in Python or the shell's Node, whichever reads simpler), run by CI in the job that validates the compose file:

- every identifier is unique across all PRDs and CSVs;
- every FF identifier of the archived PRD has a row in the map;
- every requirement has at least one acceptance criterion in the Given, When, Then form, and at least one marked `(error case)`;
- every product PRD has the sections "Supplier views" and "Records and signatures";
- each document's CSV matches its markdown (same identifiers and titles).

## Steps

1. The prompt commit, then ADR-0041 (one document per layer and product, the prefixes, the `was` column, the acceptance-criteria format, the map, the check), with its index row.
2. The map: every FF identifier with its placement, as decided above. Where a requirement does not fit the table cleanly, place it by the placement test and mark the row "placed by the test" with a sentence of reason.
3. The skeletons of every new document: control section, scope, areas, and the moved requirements under their new identifiers, text unchanged except for names that changed (layers, kernel modules).
4. `make prd-check`, run on what exists so far.

**Checkpoint: stop here and report.** Report the map's counts (moved, split, covered by the kernel, retired), every row marked "placed by the test", and the outline of each document. The design session answers with "continue" or with corrections.

5. The new requirements: the Business PRD's new areas (people, purchasing groups, departments as kinds, HSN and SAC, projects as cost objects, lookups, approvals and the delegation of authority matrix, notification templates, content visibility on masters), the expanded service receipts, TF-241 and TF-242, each with acceptance criteria in the format.
6. Every product PRD's extra sections: supplier views, records and signatures, visibility tags, dependencies, modules and permissions.
7. The five one-page scopes.
8. The technical PRD's changes (section 2.1, TF-241, TF-242, TF-099, TF-166, TF-218, sections 5a and 7, change log).
9. `docs/prd/README.md`: the documents, their prefixes, how to cite a requirement (`REQ-012 AC2`), and the map. The README's documentation list, CLAUDE.md's pointer to the requirements, and STATUS.
10. `make prd-check`, `make test`, `make shell-check` and `make format`; the report.

## Writing rules for the documents

Plain English and short sentences, as CLAUDE.md says; no em dash. A requirement says what the system shall do, not how it is built. Keep the existing text of moved requirements; change only names that changed. Mark every new requirement `[New]` in its tag, and every requirement that needs the owner's confirmation `[Decision]`, with the question in its notes. Do not invent business rules the old PRD and this file do not state: where one is needed, write it as a `[Decision]` with a proposed default.

## Review checklist

- [ ] Every FF identifier is in the map, and nothing is in two documents.
- [ ] The supplier master is in Business; the supplier life cycle is in Supplier management.
- [ ] Purchasing groups are a master, not a unit kind, and the A1 amendment is corrected.
- [ ] TF-241 has all six rules and its acceptance criteria; it is scheduled before the first business slice.
- [ ] Every product PRD names its signed records, its supplier views and how its transactions take their tags.
- [ ] `make prd-check` passes in CI.
