# Iron.Core

Shared primitives used by every module and host. No database schema. No dependencies on anything else in the repository and no third-party packages (a build check keeps it so).

| Namespace | What is in it |
|---|---|
| `Iron.Core.Results` | `Result` and `Result<T>` for expected failures, `Error` with an `ErrorKind` and a stable code, `FieldError` for validation problems, `ErrorCodes` with the code rule |
| `Iron.Core.Tenancy` | `TenantId`, the slug that identifies a tenant everywhere, and `TenantContext`, the one tenant a running unit serves |
| `Iron.Core.Identifiers` | `Ids.New()`: time-ordered UUID version 7 identifiers |
| `Iron.Core.Correlation` | `CorrelationId`: the trace id of the current request, returned to clients and shown on error pages |
| `Iron.Core.Paging` | `PageRequest`, `Page<T>`, the `ListQuery` model (sort terms, filter terms, fields), `ListFields<T>` (the fields a list exposes, with their operators) and `ListValues` (text to typed values) |
| `Iron.Core.Text` | `Naming`: the snake_case and kebab-case rules |
| `Iron.Core.Finance` | `Money` and `Currency` |
| `Iron.Core.Principals` | `ICurrentPrincipal`, who acts in the current request or job, and `SystemPrincipal` for jobs and seeds |

Conventions that come with Iron.Core (ADR-0007):

- Expected failures are values: a service returns `Result` or `Result<T>` with an `Error`. Exceptions are for programmer errors and infrastructure failures.
- Error codes are lowercase dotted segments, at least two: `identity.login.invalid_credentials`. The `Error` constructor rejects anything else.
- Time comes from `TimeProvider` (part of .NET), injected where it is needed. `DateTime.Now`, `DateTime.UtcNow` and the `DateTimeOffset` equivalents are forbidden by a build check. All stored times are UTC.
- Argument checks use the .NET throw helpers (`ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`, `ArgumentOutOfRangeException.ThrowIfNegative`). There is no `Guard` class of our own.
- Identifiers come from `Ids.New()`, never `Guid.NewGuid()`.
- Validation of request bodies is done in the API layer (T5). Iron.Core only carries the result shape (`FieldError`).

Tests: `tests/Iron.Core.Tests`.
