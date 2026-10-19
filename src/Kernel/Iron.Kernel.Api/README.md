# Iron.Kernel.Api

REST conventions for every endpoint of every host (TF-100, TF-101, ADR-0009). No schema.

| Namespace | What is in it |
|---|---|
| `Iron.Kernel.Api` | `AddIronApi()` and `UseIronApi()`: JSON shape, problem details, the last-line exception handler |
| `Iron.Kernel.Api.Modules` | `IModule` and `AddModules` / `MapModules`: how a host composes modules |
| `Iron.Kernel.Api.Permissions` | `RequirePermission` and `RequireSignedIn` on endpoints and groups, `PermissionFilter` and `SignedInFilter` (401 with `auth.not_authenticated` or `auth.session_expired`, 403), `AuthenticationFailure` for authentication middleware, `IPermissionEvaluator` for the Authorization module, `PermissionCatalogue`, `EndpointPermissionCheck` at start-up (TF-030, TF-031) |
| `Iron.Kernel.Api.Tenancy` | `TenantBinding.FromConfiguration`, `AddIronTenant`, `UseIronTenant`: the unit's one tenant from configuration and the hostname check (TF-003) |
| `Iron.Kernel.Api.Routing` | `MapApiGroup("users")`: a route group under `/api/v1/users`, tagged for OpenAPI |
| `Iron.Kernel.Api.Responses` | `ApiResults` (problem, result mapping, list envelope) and `ListResponse`, `ListMeta`, `ListLinks` |
| `Iron.Kernel.Api.Problems` | `ProblemFactory` (Error to problem details), `ApiErrors` (the API's own codes), `UnexpectedExceptionHandler` |
| `Iron.Kernel.Api.Lists` | `ListQueryParser` (query string to `ListQuery` against a resource's `ListFields`) and `FieldShaper` (sparse fields) |
| `Iron.Kernel.Api.Validation` | `Validate<TRequest>()` endpoint filter on FluentValidation and `FieldErrorMapper` |
| `Iron.Kernel.Api.OpenApi` | `AddIronOpenApi(title)` and `MapIronOpenApi()`: OpenAPI document and Scalar browser, Development only |

The conventions in one view:

- Paths: `/api/v1/{resource}` with a plural kebab-case noun. JSON: snake_case names, enums as snake_case strings, UTC times.
- Lists return `{ "data": [...], "meta": {...}, "links": {...} }`. `meta` has `page_number`, `page_size`, `total_count`, `total_pages`, `has_previous`, `has_next`, `page_size_capped`, `max_page_size`. `links` has `self`, `first`, `previous`, `next`, `last`.
- List query string: `page_number`, `page_size` (capped at 200 and reported), `sort=-created_at,name`, `filter=field:operator:value` (repeatable; operators `eq ne lt lte gt gte contains startswith in`), `fields=id,name`. Every field is declared once per resource in a `ListFields<T>`; anything else is a 400 that names the field.
- Errors are RFC 9457 problem details with `type` (`urn:iron:error:<code>`), `title`, `status`, `detail`, `instance`, and the extensions `error_code`, `correlation_id` and `errors` (field, code, message). `ErrorKind` maps to the status: Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Unexpected 500.
- Unhandled exceptions become a 500 problem with the correlation id and no internals; the exception goes to the log with the same id.
- Request bodies are validated by a FluentValidation validator registered in the module; `.Validate<TRequest>()` runs it before the handler.

An endpoint, as a module writes it:

```csharp
public static readonly ListFields<Principal> UserFields = new ListFields<Principal>()
    .Add("id", p => p.Id)
    .Add("login_id", p => p.LoginId)
    .Add("status", p => p.Status)
    .Add("created_at", p => p.CreatedAt)
    .SortByDefault("created_at", SortDirection.Descending);

var users = endpoints.MapApiGroup("users");
users.MapGet("/", async (HttpContext http, UserService service, CancellationToken ct) =>
{
    var query = ListQueryParser.Parse(http.Request.Query, UserFields);
    if (query.IsFailure)
    {
        return ApiResults.Problem(query.Error, http);
    }

    var page = await service.ListAsync(query.Value, ct);
    return ApiResults.List(page, http, query.Value);
});
users.MapPost("/", async (CreateUser request, HttpContext http, UserService service, CancellationToken ct) =>
{
    var result = await service.CreateAsync(request, ct);
    return ApiResults.From(result, http, user => TypedResults.Created($"/api/v1/users/{user.Id}", user));
}).Validate<CreateUser>();
```

Permissions on endpoints and the start-up check arrive with T8 (`RequirePermission`). A permission is checked tenant-wide unless the endpoint declares its scope (ADR-0030): `.ScopedToUnit("id")` checks at the organisation unit named by the route value, `.ScopedToRecord("order", lookup)` at the record named by the route value and the unit that owns it, which the lookup finds (`IServiceProvider`, record id, cancellation token to the owning unit id, or null). A record the lookup does not find is checked tenant-wide. The scope reaches the evaluator on `AccessRequest.Scope`.

Tests: `tests/Kernel/Iron.Kernel.Api.Tests` (an in-memory ASP.NET Core server; no Docker).
