# eProcurement Suite

The first suite on IronSaaS Foundation (ADR-0043): source to pay for regulated industries. Short name `procurement`: projects `Iron.Suites.Procurement.<Module>`, schemas `procurement_<module>`, shell modules in `shell/src/modules/suites/procurement/`, requirements in `docs/prd/suites/procurement/`.

No projects yet. The requirements are specified, and the technical PRD's section 7 proposes the slices (PR1 to PR8, after the business slices B1 to B4); TF-244 (suites per tenant) comes before the first of them.

## Shared masters

Masters that two or more of this suite's products need and no other suite would. None is decided yet: the L2 report proposes the Business requirements that only this suite needs (purchasing groups first), to be placed here or kept in Business by the design session.

## Products, in process order (ADR-0042)

| Order | Product | Requirements |
|---|---|---|
| 1 | Supplier management | `docs/prd/suites/procurement/supplier-management-PRD.md` |
| 2 | Contracts | `docs/prd/suites/procurement/contracts-PRD.md` |
| 3 | Catalogue | `docs/prd/suites/procurement/catalogue-PRD.md` |
| 4 | Requests | `docs/prd/suites/procurement/requests-PRD.md` |
| 5 | Orders | `docs/prd/suites/procurement/orders-PRD.md` |
| 6 | Receiving | `docs/prd/suites/procurement/receiving-PRD.md` |
| 7 | Invoicing | `docs/prd/suites/procurement/invoicing-PRD.md` |

A later product may use an earlier one's Contracts; an earlier one hears from a later one only through domain events.

The supplier portal is a surface above the products, not a product: each product contributes its supplier views, which obey the portal's rules, such as supplier ownership (`docs/prd/suites/procurement/supplier-portal-PRD.md`, SPX-006).

Later products, each a one-page scope until its full PRD is written: sourcing, auctions, expenses, projects and tasks, surveys (`docs/prd/suites/procurement/*-scope.md`).
