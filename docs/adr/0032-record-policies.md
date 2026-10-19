# ADR-0032: Record policies, and a forgotten policy does not compile

**Status:** Accepted
**Date:** 2026-10-19
**Decided by:** Product owner and architect

## Context

A1 gave assignments a scope and lists a predicate (`WhereAccessible`, ADR-0030); A2 used it for the users list and answered "not found" for a user outside the reader's scope (ADR-0031). TF-035 asks for the rule every business record follows: a module declares a record-level policy (the owner may edit while a draft, a supplier sees only its own records, an approver sees what it must approve, unit visibility), the service evaluates it for reads by id, lists apply it as a filter so paging counts are right, and a record without a policy fails the build. A check that relies on every endpoint remembering it is the gap TF-035 names; the compiler and the architecture tests must catch the forgetting.

## Decision

1. **The interface.** `IRecordPolicy<T> where T : BusinessRecord` has two members: `ReadableAsync(subject)` answers an expression of the records the subject may read, which runs in the database; `DecideAsync(record, action, subject)` answers, for one loaded record and an action (`read`, `update`, `delete`, or a module's own name such as `approve`), `Allowed`, `NotAllowed`, `NotVisible` or `NoRule`. A `PolicySubject` carries the principal and what each of its permissions reaches for the record type (`IRecordAccess`, computed once per permission).
2. **Where it lives.** The policy types and their engine live in `Iron.Kernel.Data`, beside `BusinessRecord`, not in a Contracts project: a Contracts project may reference the vocabulary only (ADR-0001), and the constraint needs `BusinessRecord`. `Iron.Kernel.Data` is a kernel library every module may reference, so it is as public as a Contracts project. `IRecordAccess` (what one permission reaches, ADR-0030) moves there from Authorization's Contracts for the same reason; Authorization still implements it.
3. **Registration.** A module registers one policy per business record type in `AddServices`: `services.AddRecordPolicy<Order>("purchase_order", OrdersPermissions.Read, policy)`, naming the record type that record-scoped assignments use and the permission that reads it.
4. **Composition with the scope rule.** The kernel applies A1's scope rule first, for the registered read permission: the owning unit within the reader's reach, or the record granted by itself. The module's filter is joined to it with AND, in the same query. A policy can therefore narrow what the scope rule shows, and can never widen it, whatever its filter says. The building blocks compose the same way: `All` joins with AND and takes the strictest answer, `Any` joins with OR and takes the most generous; `NoRule` (a block with nothing to say about an action) is skipped by both, and at the top it means "not allowed": deny by default.
5. **A forgotten policy does not compile.** Paging a raw `IQueryable<T>` needs `T : ITenantWide`. A business record is never `ITenantWide` (the model check refuses an entity that is both), so `context.Orders.ToPageAsync(...)` fails with CS0311. A business record is paged only through `PolicyQuery<T>`, whose constructor is internal to `Iron.Kernel.Data`; the only way to get one is `IRecordPolicies.ApplyAsync(query)` or `For<T>(principal).ApplyAsync(query)`, which applies the scope rule and the policy. `PolicyQuery<T>` narrows further (`Where`, `ApplyListQuery`) and pages, counts or lists; it never hands the raw query back. A test compiles both forms with Roslyn: the raw one fails with CS0311, the policy one compiles.
6. **Reads by id: 404 and 403.** `IRecordPolicies.FindAsync<T>(id, action)` loads the record through the same filter. A record the reader may not read answers not found (`data.record.not_found`, 404), the same as an id that does not exist, so a link reveals nothing. A record the reader may read but not act on answers forbidden (`data.record.forbidden`, 403). `read` is allowed once the record is found; any other action goes to `DecideAsync`, and `NotAllowed` or `NoRule` are forbidden, `NotVisible` is not found. The record comes back tracked by the module's own context, so the caller can change it and save.
7. **The build check.** An architecture test reads every module assembly: each concrete `BusinessRecord` type must have a policy that the module's `AddServices` registers. A fixture with a record and no policy proves the refusal. With rule 5 this is TF-035 AC3.
8. **Kernel building blocks**, so a policy is a few lines: `RecordRules.UnitVisibility<T>(read, (action, permission)...)` (the owning unit within the reach of the action's permission), `RecordRules.OwnerWhile<T>(predicate, actions)` (the creator may perform the actions while the module's predicate holds, for example while a draft; reading is not narrowed), `RecordRules.All` and `RecordRules.Any`. Supplier and approver policies are not kernel work: they belong to the Products layer and to the workflow slice.
9. **Kernel records today.** No kernel entity is a business record. Principals, units and role assignments keep their A1 and A2 scope checks, which already answer not found; the kernel entities that lists page are marked `ITenantWide`.

## Options considered

- A Roslyn analyzer that flags `ToPageAsync` on a business record. Rejected for now: a new analyzer project and package for a rule the type system can state; the constraint gives the same compile error.
- Two `ToPageAsync` overloads, one constrained to `BusinessRecord`. Rejected: the call becomes ambiguous (CS0121) with a message that does not say what to do, and a static call names the open overload directly.
- An architecture test that scans the IL for `ToPageAsync` over a business record. Rejected as the main guard: it finds the mistake after the build; kept possible later for other helpers.
- The interface in Authorization's Contracts. Rejected: Contracts may reference the vocabulary only, and the constraint needs `BusinessRecord`.
- Answer 403 for a record outside the scope (TF-035 AC1 as first written). Rejected: it tells the reader that the record exists. AC1 now says not found.
- Policies that may widen the scope rule (an approver who sees records of other units). Rejected for the kernel: widening is access, which is an assignment (ADR-0030) or a share (A8), audited where it is granted.

## Consequences

- Easier: a module writes a policy in a few lines from the blocks, and cannot page or open one of its records without it; the reader of the code sees the scope rule and the policy in one place.
- Harder: a list of a non-business entity needs `ITenantWide` on the entity to page; a projection is paged before it is mapped (`Page.Map`). `PolicyQuery<T>` offers what lists and services need today (`Where`, `ApplyListQuery`, `AsNoTracking`, `ToPageAsync`, `ToListAsync`, `CountAsync`); a new need adds a member rather than an escape.
- The guarantee covers paging and reads by id. A module can still read its own tables with Entity Framework for background work; such reads must not reach a response, and review keeps it so.
- A record-level refusal is logged with the principal, the record and the action; the permission filter keeps auditing permission denials (ADR-0012). Revisit when the audit of reads arrives (A8).
- Revisit with the workflow slice (approvers) and the supplier portal (supplier policies), which add Products-layer blocks on this interface.
