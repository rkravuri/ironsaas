# Iron.Kernel.Api

REST conventions for every endpoint of every host (TF-100, TF-101, ADR-0009). No schema.

| Namespace | What is in it |
|---|---|
| `Iron.Kernel.Api` | `AddIronApi()` and `UseIronApi()`: JSON shape, problem details, the last-line exception handler |
| `Iron.Kernel.Api.Modules` | `IModule` (with its `Licence` and `Features`, ADR-0034) and `AddModules` / `MapModules`: how a host composes modules; `LicensingCatalogue`, what the host's modules can license, which the control plane reads from the file `make catalogue` writes |
| `Iron.Kernel.Api.Flags` | `IFlagGate` for the Flags module, `RequireFlagOff("kill.imports")` and `RequireFlag("area.flag")`, `FlagMetadata`, `FlagCheck`, `FlagFilter` and `FlagErrors` (`flags.killed`, 503 with `Retry-After`; `flags.disabled`, 404) (ADR-0035) |
| `Iron.Kernel.Api.Context` | `IContextPart` and `AddContextPart`: the parts of `GET /api/v1/me/context` (entitlements, flags), which MapModules maps once when a module registered a part |
| `Iron.Kernel.Api.Entitlements` | `IEntitlementGate` for the Entitlements module, `RequireFeature("area.feature")`, `LicensedModuleMetadata` (MapModules puts it on every endpoint of a licensed module), `EntitlementCheck`, `EntitlementFilter` and `EntitlementErrors` (`entitlements.module_disabled`, `entitlements.read_only`, `entitlements.feature_disabled`, `entitlements.seat_required`) |
| `Iron.Kernel.Api.Permissions` | `RequirePermission` and `RequireSignedIn` on endpoints and groups, `PermissionFilter` and `SignedInFilter` (401 with `auth.not_authenticated` or `auth.session_expired`, 403), `AuthenticationFailure` for authentication middleware, `IPermissionEvaluator` for the Authorization module, `PermissionCatalogue`, `EndpointPermissionCheck` at start-up (TF-030, TF-031); `RequireRecentAuthentication(minutes)` and its filter, which ask the kernel's `IRecentAuthentication` and answer 403 `auth.reauthentication_required` when the caller's authentication is older (TF-248, ADR-0047); `RequiredStep`, a step an authentication middleware says the principal must take first, which the filters answer for every endpoint but those declared `AllowDuringRequiredStep()` (ADR-0047) |
| `Iron.Kernel.Api.Tenancy` | `TenantBinding.FromConfiguration`, `AddIronTenant`, `UseIronTenant`: the unit's one tenant from configuration and the hostname check (TF-003) |
| `Iron.Kernel.Api.Routing` | `MapApiGroup("users")`: a route group under `/api/v1/users`, tagged for OpenAPI |
| `Iron.Kernel.Api.Responses` | `ApiResults` (problem, result mapping, list envelope) and `ListResponse`, `ListMeta`, `ListLinks` |
| `Iron.Kernel.Api.Problems` | `ProblemFactory` (Error to problem details), `ApiErrors` (the API's own codes), `UnexpectedExceptionHandler` |
| `Iron.Kernel.Api.Lists` | `ListQueryParser` (query string to `ListQuery` against a resource's `ListFields`) and `FieldShaper` (sparse fields) |
| `Iron.Kernel.Api.Validation` | `Validate<TRequest>()` endpoint filter on FluentValidation and `FieldErrorMapper`; `RequestLimitCheck`, the start-up check that every request field has a declared limit, and the rules it reads besides FluentValidation's lengths: `MaximumCount(n)` for a list, `MaximumJsonSize(bytes)` for a free JSON value; `FieldLimits`, the limits of fields many modules share; `QueryLimitMiddleware`, the query's limits (TF-071 AC1, ADR-0053) |
| `Iron.Kernel.Api.Http` | `ClientAddress` (the caller's address behind the ingress), `RateLimited(policy)` (the rate limits an endpoint is under besides the API's own, which the Protection module counts) and `LimitBodySize(bytes)` for a body the handler reads itself (ADR-0053) |
| `Iron.Kernel.Api.OpenApi` | `AddIronOpenApi(title)` and `MapIronOpenApi()`: OpenAPI document and Scalar browser, Development only |

The conventions in one view:

- Paths: `/api/v1/{resource}` with a plural kebab-case noun. JSON: snake_case names, enums as snake_case strings, UTC times.
- Lists return `{ "data": [...], "meta": {...}, "links": {...} }`. `meta` has `page_number`, `page_size`, `total_count`, `total_pages`, `has_previous`, `has_next`, `page_size_capped`, `max_page_size`. `links` has `self`, `first`, `previous`, `next`, `last`.
- List query string: `page_number`, `page_size` (capped at 200 and reported), `sort=-created_at,name`, `filter=field:operator:value` (repeatable; operators `eq ne lt lte gt gte contains startswith in`), `fields=id,name`. Every field is declared once per resource in a `ListFields<T>`; anything else is a 400 that names the field.
- Errors are RFC 9457 problem details with `type` (`urn:iron:error:<code>`), `title`, `status`, `detail`, `instance`, and the extensions `error_code`, `correlation_id` and `errors` (field, code, message). `ErrorKind` maps to the status: Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Unexpected 500.
- Unhandled exceptions become a 500 problem with the correlation id and no internals; the exception goes to the log with the same id.
- Request bodies are validated by a FluentValidation validator registered in the module; `.Validate<TRequest>()` runs it before the handler.
- Every request field has a declared limit (TF-071 AC1, ADR-0053). At start the host reads each endpoint's body through its validator: a string needs a maximum length, a list `MaximumCount` and its items their limits, a free JSON value `MaximumJsonSize`, an object a validator of its own (`SetValidator` or `ChildRules`). Numbers, dates, identifiers, flags and enumerations are limited by their types; a body of only those needs no validator. A handler that reads its body itself declares `LimitBodySize`. A host with a field without a limit does not start, and the message names the endpoint and the field; the host tests start every host, so the build fails. The query is limited for every request: 50 parameters, 2,000 characters each. A request over any limit is a 400 `api.validation.failed` whose `errors` name the field (`maximum_length`, `maximum_count`).
- A rate limit's refusal is a 429 with `Retry-After`, `error_code` `protection.rate_limited` and `retry_after_seconds` (TF-072, ADR-0053).

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

Flags (ADR-0035): an endpoint's flags are checked after the entitlements and before the permission, in the same filters. The start-up check refuses a flag no module of the host declares (the kill switches are declared for every host) and a flagged endpoint without an `IFlagGate`.

Entitlements (ADR-0034): a licensed module's endpoints and an endpoint with `RequireFeature` are checked after authentication and before the permission, inside the permission and sign-in filters, so a person whose tenant lacks the module hears that and no permission denial is written for it; an anonymous endpoint is checked by `EntitlementFilter`. The start-up check also refuses a feature no module of the host declares, and a licensed endpoint without an `IEntitlementGate`.

Permissions on endpoints and the start-up check arrive with T8 (`RequirePermission`). A permission is checked tenant-wide unless the endpoint declares its scope (ADR-0030): `.ScopedToUnit("id")` checks at the organisation unit named by the route value, `.ScopedToRecord("order", lookup)` at the record named by the route value and the unit that owns it, which the lookup finds (`IServiceProvider`, record id, cancellation token to the owning unit id, or null). A record the lookup does not find is checked tenant-wide. The scope reaches the evaluator on `AccessRequest.Scope`.

Tests: `tests/Kernel/Iron.Kernel.Api.Tests` (an in-memory ASP.NET Core server; no Docker).

The unit's control-plane client (ADR-0050, ADR-0054): `Iron.Kernel.Api.ControlPlane` holds `ControlPlaneOptions` (`ControlPlane:Address`, `ClientId`, `ClientSecret`) and `ControlPlaneClient` (a client credentials token kept until a minute before it ends), one per process: the Audit module sends its anchors through it, the Keys module reads its rotations and reports its re-encryption.
