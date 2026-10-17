# ADR-0007: Core primitives: results and errors, tenant identifier, ids, time, correlation

**Status:** Accepted
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

Every module needs the same small set of primitives: a way to return expected failures, a stable identifier for a tenant, identifiers for records, the current time, and the correlation id of a request (PRD section 2.1, `Iron.Core`). These choices are made once in Iron.Core and inherited by everything else, so they are recorded here.

## Decision

- Expected failures are values, not exceptions. A service returns `Result` or `Result<T>`; a failure carries an `Error` with an `ErrorKind` (Validation, Unauthorized, Forbidden, NotFound, Conflict, Unexpected), a stable code and a message, plus `FieldError`s for validation. The API layer maps the kind to the HTTP status and the error to problem details (TF-100). Exceptions remain for programmer errors (argument checks with the .NET throw helpers) and infrastructure failures.
- Error codes are lowercase dotted segments, at least two, letters, digits and underscores, each segment starting with a letter: `identity.login.invalid_credentials`. The `Error` constructor rejects anything else, so a typo fails a test, not a customer.
- The tenant identifier is a slug chosen at creation and never changed: 3 to 30 characters, lowercase letters, digits and single hyphens, starting with a letter (`acme`). It is the same in every environment (TF-001) and names the tenant's database, containers, secrets path and default hostname. `control` is reserved for the control plane; `network` will be reserved for the Supplier Network. The fleet maps hyphens to underscores where PostgreSQL identifiers need it.
- Record identifiers are UUID version 7 (`Ids.New()`): random but ordered by creation time, which keeps indexes compact at the scale targets of P15. `Guid.NewGuid()` is not used for keys.
- Time comes from .NET's `TimeProvider`, injected where needed; Foundation has no clock interface of its own. A build check fails any Foundation assembly that reads `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now` or `DateTimeOffset.UtcNow` directly. Stored times are UTC.
- The correlation id is the W3C trace id of the current activity (`CorrelationId.Current()`), so the id in the response header, the logs, the trace and the error page are one and the same (TF-130).
- Paging is offset based in S1 (`PageRequest`, `Page<T>`; page size capped at 200 and the cap reported). The list grammar model (`ListQuery` with sort terms, filter terms and fields) lives in Iron.Core; parsing is the API layer's job and applying it is the data layer's.
- `Money` and `Currency` exist as plain value types with same-currency arithmetic and commercial rounding; minor-unit tables and conversion belong to products.
- Iron.Core depends on nothing: no project reference, no package. A build check keeps it so (the module boundary rule allows a shared library to reference shared libraries only; Iron.Core references none).

## Options considered

- Exceptions for business failures. Rejected: control flow by exception is slow and hides the failure cases from the reader.
- A result library (for example a NuGet package with monadic helpers). Rejected: one more dependency and one more dialect to learn; two small classes are enough.
- A random identifier for tenants with a separate display slug. Rejected: two names for one thing, and operators work with the slug anyway.
- An own `IClock` interface. Rejected: `TimeProvider` is in .NET and has a fake for tests in a Microsoft package.
- Strongly typed record ids (`UserId`, `OrderId`). Deferred: safer, but more friction for a new developer and for EF mapping; revisit after S2 when the first product shows whether plain `Guid` ids cause mistakes.

## Consequences

- Easier: every module reads the same way; tests control time and ids; errors are predictable for the UI and partners.
- Harder: developers must remember to inject `TimeProvider`; the build check tells them when they forget.
- Revisit: cursor paging for very large lists (S9), strongly typed ids after S2.
