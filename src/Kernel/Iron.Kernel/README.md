# Iron.Kernel

The kernel vocabulary: the shared primitives every module and host speaks (ADR-0007, ADR-0029). No database schema. No dependencies on anything else in the repository and no third-party packages (a build check keeps it so).

Every type is in one namespace, `Iron.Kernel`, so that no second segment under `Iron.Kernel` can be both a vocabulary area and a module. The folders group the files only:

| Folder | What is in it |
|---|---|
| `Results/` | `Result` and `Result<T>` for expected failures, `Error` with an `ErrorKind` and a stable code, `FieldError` for validation problems, `ErrorCodes` with the code rule |
| `Tenancy/` | `TenantId`, the slug that identifies a tenant everywhere, and `TenantContext`, the one tenant a running unit serves |
| `Identifiers/` | `Ids.New()`: time-ordered UUID version 7 identifiers |
| `Correlation/` | `CorrelationId`: the trace id of the current request, returned to clients and shown on error pages |
| `Paging/` | `PageRequest`, `Page<T>`, the `ListQuery` model (sort terms, filter terms, fields), `ListFields<T>` (the fields a list exposes, with their operators) and `ListValues` (text to typed values) |
| `Text/` | `Naming`: the snake_case and kebab-case rules |
| `Finance/` | `Money` and `Currency` |
| `Principals/` | `ICurrentPrincipal`, who acts in the current request or job, `SystemPrincipal` for jobs and seeds, and `AnonymousPrincipal` |
| `Permissions/` | `Permission` (`module.resource.action`, description, `Risk`) |

A new type goes into the folder of its area and the namespace `Iron.Kernel`. Code inside `Iron.Kernel.*` sees the vocabulary without a `using`; hosts, tests outside the kernel and tools write `using Iron.Kernel;`.

Conventions that come with the vocabulary (ADR-0007):

- Expected failures are values: a service returns `Result` or `Result<T>` with an `Error`. Exceptions are for programmer errors and infrastructure failures.
- Error codes are lowercase dotted segments, at least two: `identity.login.invalid_credentials`. The `Error` constructor rejects anything else.
- Time comes from `TimeProvider` (part of .NET), injected where it is needed. `DateTime.Now`, `DateTime.UtcNow` and the `DateTimeOffset` equivalents are forbidden by a build check. All stored times are UTC.
- Argument checks use the .NET throw helpers (`ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`, `ArgumentOutOfRangeException.ThrowIfNegative`). There is no `Guard` class of our own.
- Identifiers come from `Ids.New()`, never `Guid.NewGuid()`.
- Validation of request bodies is done in the API layer (`Iron.Kernel.Api`). The vocabulary only carries the result shape (`FieldError`).

Tests: `tests/Kernel/Iron.Kernel.Tests`.
