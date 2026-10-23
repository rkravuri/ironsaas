# Risk classes of the requirements

The validation pack of every release classes each change by the highest risk of the requirements it touched (TF-218, ADR-0052). A requirement is **critical** when its area is listed below, **high** when it is any other Must, and **normal** when it is a Should or a Could. A change that touched no requirement is normal. `tools/validation_pack.py` reads this table; the design session changes it, with the reason, like the exceptions beside it.

Critical areas are those a validated system relies on for the integrity of its records and the control of who may do what: a defect there could let the wrong person act, change a record without trace, or pay the wrong party.

| Area | Class | Why |
|---|---|---|
| Tenancy and control plane | critical | One tenant's data and units are isolated from another's; the control plane holds the anchors that prove the audit chain. |
| Identity and authentication | critical | Who a person is: sign-in, passwords, second factors, company logins, step-up. |
| Sessions, tokens and service accounts | critical | How long that proof lasts, and what a client may do with it. |
| Authorization | critical | Who may do what, where; segregation of duties; delegation. |
| Audit and compliance | critical | The trail, its chain, field history, signatures, retention and holds: the records an auditor relies on. |
| Security | critical | The platform's defences: secure defaults, rate limits, keys and encryption. |
| Data platform | critical | How records are stored, scoped to their units and changed with their version. |
| Regulated-industry controls | critical | Validation support and signature manifestation, which Part 11 and Annex 11 tenants depend on. |
| Governed changes | critical | Controlled changes to masters, the path to bank details and other critical fields. |
| Visibility and access | critical | Which records a person may see beyond their roles. |
| Approval and payment | critical | Releasing money to suppliers. |
| Verification and screening | critical | Checking a supplier's identity, bank account and sanctions status before paying them. |
| Supplier information change management | critical | Changes to a supplier's bank and legal details, where fraud begins. |
| Approvals and delegation of authority | critical | Who may commit the organisation's money. |
| Matching and controls | critical | The checks between orders, receipts and invoices before payment. |
