# ADR-0042: Dependencies go down the layers, and along the process between products

**Status:** Accepted
**Date:** 2026-10-20
**Decided by:** Product owner and architect

## Context

ADR-0029 lets code depend only downward: products on business and kernel, business on kernel, and between modules only through a Contracts project. It says nothing about one product depending on another. At the checkpoint of the PRD split (ADR-0041), the moved requirements showed both gaps. Four Business requirements depended on product requirements (supplier enablement on supplier approval, the go-live checklist on supplier onboarding, the country packs on e-invoicing, the reference data on payment status). Products depended on each other in both directions: Requests and Orders, Orders and Contracts, Receiving and Invoicing. Project references cannot form a cycle, so two products that call each other do not build, and requirements that cite each other both ways hide which product owns a behaviour. The design session decided the rule on 2026-10-20.

## Decision

1. **Requirements depend down the layers, as code does.** The kernel depends on the kernel; the business layer on the kernel; the products on the business layer and the kernel; the supplier portal surface on all of them. The product method (`product-method.md`) binds every layer and stands outside the order.
2. **Business never depends on a product.** A clause that belongs in a product moves there and uses the business requirement below it. Only where Business must hear from a product does Business declare an extension point, an interface in its Contracts that the product implements; both documents say so, the business one where it declares the point and the product one in its Dependencies section.
3. **Products depend on each other in the process order:** Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing. A later product may call an earlier one through the earlier one's Contracts project. An earlier product hears from a later one only through domain events (TF-110 to TF-114): the later product publishes, the earlier one subscribes. Receiving publishes goods received, and Orders updates its open quantities.
4. **Which way a behaviour goes.** When an earlier product's behaviour needs a later one, either the later product does it and calls the earlier one (Orders records a contract release through Contracts when it issues an order line), or the earlier product publishes an event the later one handles (a request converted into orders becomes draft orders when Orders handles the conversion). A check that must be atomic, such as a contract ceiling or an open quantity, is made by the later product calling the earlier one; an event is for what may follow a moment later.
5. **The supplier portal surface sits above the products.** It composes their supplier views and states the rules every view obeys, such as supplier ownership (SPX-006). A product's supplier view cites the rule in its notes and depends on the kernel requirements that enforce it (TF-035, TF-234), not on the surface.
6. **How a requirement writes it.** A dependency on a later product is written with the mark `(event)`: `RCV-081 (event)`. Each product PRD's Dependencies section lists what it calls (the earlier products, the business layer, the kernel), the events it publishes, the events it subscribes to, and the business extension points it implements.
7. **The check holds it.** `make prd-check` refuses a dependency that points up a layer, a dependency on a later product without `(event)`, and the mark on anything else.
8. **New products take a place in the order** when their full PRD is written, by the same rule; their scope pages propose one.

## Options considered

- Products may depend on each other freely through Contracts. Rejected: two products that need each other form a cycle of project references, which does not build, and nobody can say which owns a behaviour.
- Products talk only through events. Rejected: a contract ceiling, an open quantity or a budget must be checked inside the transaction that consumes it, which an event cannot do.
- One product module for all of procure-to-pay. Rejected: each product has its own documents and life cycle (ADR-0029), its own licence (ADR-0034) and its own team later.
- Let Business call products through events too. Rejected: the business layer would then know the products' events, and every new product would change Business; an extension point the product implements keeps the dependency pointing down.

## Consequences

- Easier: the order of the product slices follows from the dependencies; a product's Dependencies section says everything it needs and everything it tells; the build will refuse a cycle the documents already refuse.
- Harder: an earlier product that shows a later product's data (Supplier management's open documents of a supplier, Contracts' coverage report) keeps what it needs from events, so its view may trail by the event's delivery time (TF-085, within seconds); a conversion into a later product's document is asynchronous.
- Revisit: when the five later products get their PRDs, their places in the order; if an event turns out to need an answer at once, the behaviour moves to the later product.
