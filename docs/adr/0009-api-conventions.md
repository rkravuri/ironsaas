# ADR-0009: API conventions

**Status:** Accepted
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

TF-100 asks for REST resources with plural nouns, snake_case JSON, a standard envelope for lists, problem details for errors with a stable code and the correlation id, and OpenAPI generated from code. TF-101 asks for one list grammar (paging, filtering, sorting, sparse fields) implemented once and whitelisted per resource. P9 says every screen uses the same public API that partners use.

## Decision

- Paths: `/api/v1/{resource}` with a plural kebab-case noun (`users`, `purchase-orders`). Versioning by path; `v1` is the only version in S1.
- JSON: snake_case property names, enums as snake_case strings, times in UTC, nulls written. One `JsonSerializerOptions` for the whole host (`IronJson`).
- Lists return `{ data, meta, links }`. `meta` carries the paging facts including whether the page size was capped; `links` carry `self`, `first`, `previous`, `next`, `last` as relative URLs that keep every other query parameter.
- The list grammar: `page_number`, `page_size` (max 200, capped and reported), `sort=-created_at,name`, `filter=field:operator:value` (repeatable; operators `eq ne lt lte gt gte contains startswith in`; `in` takes comma separated values), `fields=id,name`. The fields a resource exposes are declared once in a `ListFields<T>` next to the endpoint; the parser (`Iron.Api`) refuses unknown fields, wrong operators and bad values with a 400 that names the query parameter and the field, and the data layer (`Iron.Data.Lists`) applies the parsed query to an `IQueryable`. Text filters ignore case. Lists always have a stable order: the caller's or the resource's default.
- Errors are RFC 9457 problem details. `type` is `urn:iron:error:<code>`, `error_code` repeats the stable code, `correlation_id` is the trace id, `errors` lists field problems as `field`, `code`, `message` in the client's JSON names. `ErrorKind` maps to the status: Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Unexpected 500. Problems the framework produces on its own (bad JSON, missing route) get the same extensions.
- Unhandled exceptions become a 500 problem with the correlation id and a fixed message; the exception goes to the log with the same id. Nothing internal reaches the client.
- Request bodies are validated with FluentValidation. A validator is registered per request type in the module; `.Validate<TRequest>()` runs it before the handler and a missing validator is an error, not a silent pass. Validator names become short codes (`not_empty`), property paths become snake_case JSON paths.
- Modules plug into hosts through `IModule` (`Name`, `AddServices`, `MapEndpoints`); hosts list their modules explicitly and call `AddModules` and `MapModules`. Permissions join the interface in T8.
- OpenAPI is generated from the endpoints by `Microsoft.AspNetCore.OpenApi` and browsed with Scalar. Both are mapped in Development only, because they are not on the anonymous allow-list of TF-030; a permission-guarded production version can follow.

## Options considered

- Controllers with MVC. Rejected: minimal APIs are the plain layer P1 asks for; endpoint filters cover validation and permissions.
- Camel case JSON. Rejected: the database, the API and the UI share snake_case, so one name travels end to end.
- A query language such as OData or GraphQL. Rejected: a bounded grammar is enough for grids and partners and is far simpler to secure (TF-101: whitelist per resource).
- Swashbuckle for OpenAPI. Rejected: the built-in generator is maintained with the framework and needs no extra package.
- .NET's built-in minimal API validation with data annotations. Rejected for now: FluentValidation rules read better and carry codes; the built-in one can be revisited if the dependency becomes a burden.

## Consequences

- Easier: every endpoint reads the same way, every error looks the same to the UI and partners, grids get server paging and filtering for free.
- Harder: each list operation must declare its fields; this is the point (TF-101).
- Revisit: cursor paging for very large lists (S9); production OpenAPI behind a permission (S7, partner API).
