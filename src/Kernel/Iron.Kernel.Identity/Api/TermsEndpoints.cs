using System.Globalization;
using System.Text;
using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>One document version as a person reads it, and as the console lists it.</summary>
public sealed record DocumentResponse(string Source, string Key, int Version, string Title, string Body, DateTimeOffset PublishedAt);

/// <summary>The versions the person must accept before anything else, oldest source first: the platform's, then the tenant's.</summary>
public sealed record MyTermsResponse(IReadOnlyList<DocumentResponse> Pending);

public sealed record DocumentChoiceRequest(string Source, string Key, int Version);

/// <summary>The versions the person accepts: every pending one, at its current version.</summary>
public sealed record AcceptTermsRequest(IReadOnlyList<DocumentChoiceRequest> Documents);

public sealed class AcceptTermsRequestValidator : AbstractValidator<AcceptTermsRequest>
{
    public AcceptTermsRequestValidator()
    {
        RuleFor(r => r.Documents).NotEmpty();
        RuleForEach(r => r.Documents).ChildRules(document =>
        {
            document.RuleFor(d => d.Source).NotEmpty().MaximumLength(DocumentAcceptance.SourceMaxLength);
            document.RuleFor(d => d.Key).NotEmpty().MaximumLength(TenantDocument.KeyMaxLength);
            document.RuleFor(d => d.Version).GreaterThan(0);
        });
    }
}

/// <summary>A tenant document's next version, or the first one of a new key.</summary>
public sealed record PublishDocumentRequest(string Key, string Title, string Body);

public sealed class PublishDocumentRequestValidator : AbstractValidator<PublishDocumentRequest>
{
    public PublishDocumentRequestValidator()
    {
        RuleFor(r => r.Key).NotEmpty().MaximumLength(TenantDocument.KeyMaxLength);
        RuleFor(r => r.Title).NotEmpty().MaximumLength(TenantDocument.TitleMaxLength);
        RuleFor(r => r.Body).NotEmpty().MaximumLength(TenantDocument.BodyMaxLength);
    }
}

/// <summary>How many people accepted a document's current version, beside the version itself.</summary>
public sealed record DocumentSummaryResponse(string Source, string Key, int Version, string Title, DateTimeOffset PublishedAt, int Accepted, string Body)
{
    /// <summary>A tenant document version's id, whose field-level history the Documents page shows (TF-062); null for the platform's.</summary>
    public Guid? Id { get; init; }
}

/// <summary>The documents people must accept now: a handful, so one answer rather than a page.</summary>
public sealed record DocumentsResponse(IReadOnlyList<DocumentSummaryResponse> Documents);

/// <summary>An acceptance as the console lists it: who, which version of which document, when (TF-028 AC2).</summary>
public sealed class AcceptanceRow : IOwnAccessRule
{
    public Guid Id { get; init; }

    public Guid PrincipalId { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string DocumentKey { get; init; } = string.Empty;

    public int Version { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>
/// Terms acceptance (TF-028, ADR-0049). A person reads what they must accept and accepts or declines it, while the
/// required step holds everything else back; declining ends the session. The console's Documents page publishes the
/// tenant's versions and lists who accepted which, with a CSV export, for the holders of <c>identity.documents.manage</c>.
/// </summary>
internal static class TermsEndpoints
{
    /// <summary>The longest export: an administrator narrows it with the list's filters rather than reading a million rows at once.</summary>
    public const int ExportMaxRows = 50_000;

    public static readonly ListFields<AcceptanceRow> Fields = new ListFields<AcceptanceRow>()
        .Add("id", a => a.Id, FieldUse.Select | FieldUse.Filter)
        .Add("principal_id", a => a.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("login_id", a => a.LoginId)
        .Add("display_name", a => a.DisplayName)
        .Add("source", a => a.Source, FieldUse.Select | FieldUse.Filter)
        .Add("document_key", a => a.DocumentKey, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("version", a => a.Version, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("accepted_at", a => a.AcceptedAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .SortByDefault("accepted_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapGroup("/api/v1/me/terms").WithTags("me");
        me.MapGet("/", MineAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.TermsAcceptanceRequired).Produces<MyTermsResponse>(StatusCodes.Status200OK).WithName("MyTerms").WithSummary("The document versions you must accept before anything else, with their text.");
        me.MapPost("/accept", AcceptAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.TermsAcceptanceRequired).ForbiddenDuringImpersonation().Validate<AcceptTermsRequest>().Produces(StatusCodes.Status204NoContent).WithName("AcceptTerms").WithSummary("Accept every version you must, as you read it; nothing is recorded when a newer one was published meanwhile.");
        me.MapPost("/decline", DeclineAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.TermsAcceptanceRequired).ForbiddenDuringImpersonation().Produces(StatusCodes.Status204NoContent).WithName("DeclineTerms").WithSummary("Decline: your session ends, and you are asked again at your next sign-in.");

        var documents = endpoints.MapApiGroup("documents");
        documents.MapGet("/", DocumentsAsync).RequirePermission(IdentityPermissions.DocumentsManage).Produces<DocumentsResponse>(StatusCodes.Status200OK).WithName("ListDocuments").WithSummary("The documents people must accept: the platform's and the tenant's current versions, and how many people accepted each.");
        documents.MapPost("/", PublishAsync).RequirePermission(IdentityPermissions.DocumentsManage).Validate<PublishDocumentRequest>().Produces<DocumentResponse>(StatusCodes.Status201Created).WithName("PublishDocument").WithSummary("Publish a tenant document's next version, or the first of a new one; everyone accepts it at their next request.");
        // An administrator held by the document's own new version may still retire it, a mistaken one above all.
        documents.MapPost("/{key}/retire", RetireAsync).RequirePermission(IdentityPermissions.DocumentsManage).AllowDuringRequiredStep(IdentityErrors.TermsAcceptanceRequired).Produces(StatusCodes.Status204NoContent).WithName("RetireDocument").WithSummary("Retire a tenant document: nobody is asked to accept it any more, and the acceptances stay.");
        documents.MapGet("/acceptances", AcceptancesAsync).RequirePermission(IdentityPermissions.DocumentsManage).Produces<ListResponse<AcceptanceRow>>(StatusCodes.Status200OK).WithName("ListAcceptances").WithSummary("Who accepted which version of which document, and when.");
        documents.MapGet("/acceptances/export", ExportAsync).RequirePermission(IdentityPermissions.DocumentsManage).Produces<string>(StatusCodes.Status200OK, "text/csv").WithName("ExportAcceptances").WithSummary("The acceptances as CSV, with the list's filters and sort.");
    }

    private static async Task<IResult> MineAsync(RequestPrincipal principal, TermsService terms, CancellationToken cancellationToken)
    {
        var pending = await terms.PendingAsync(principal.Current!.Id, cancellationToken);
        return TypedResults.Ok(new MyTermsResponse([.. pending.Select(d => new DocumentResponse(d.Source, d.Key, d.Version, d.Title, d.Body, d.PublishedAt))]));
    }

    private static async Task<IResult> AcceptAsync(AcceptTermsRequest request, HttpContext http, RequestPrincipal principal, IdentityDbContext context, TermsService terms, CancellationToken cancellationToken)
    {
        var person = await context.Principals.SingleAsync(p => p.Id == principal.Current!.Id, cancellationToken);
        var result = await terms.AcceptAsync(person, [.. request.Documents.Select(d => new DocumentChoice(d.Source, d.Key, d.Version))], cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static async Task<IResult> DeclineAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, TermsService terms, SessionService sessions, IdentityOptions options, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        var person = await context.Principals.SingleAsync(p => p.Id == current.Id, cancellationToken);
        await terms.DeclinedAsync(person, cancellationToken);
        if (current.Kind == AuthenticationKind.Session)
        {
            await sessions.EndAsync(current.SessionId, SessionEndReasons.TermsDeclined, cancellationToken);
            SessionCookie.Clear(http, options);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> DocumentsAsync(IdentityDbContext context, TermsService terms, CancellationToken cancellationToken)
    {
        var current = await terms.CurrentAsync(cancellationToken);
        var counts = await context.DocumentAcceptances.AsNoTracking()
            .GroupBy(a => new { a.Source, a.DocumentKey, a.Version })
            .Select(g => new { g.Key.Source, g.Key.DocumentKey, g.Key.Version, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new DocumentsResponse([.. current.Select(d => new DocumentSummaryResponse(
            d.Source,
            d.Key,
            d.Version,
            d.Title,
            d.PublishedAt,
            counts.FirstOrDefault(c => c.Source == d.Source && c.DocumentKey == d.Key && c.Version == d.Version)?.Count ?? 0,
            d.Body) { Id = d.Id })]));
    }

    private static async Task<IResult> PublishAsync(PublishDocumentRequest request, HttpContext http, TermsService terms, CancellationToken cancellationToken)
    {
        var result = await terms.PublishAsync(request.Key, request.Title, request.Body, cancellationToken);
        return ApiResults.From(result, http, d => TypedResults.Created($"{ApiRouting.Prefix}/documents/{d.Key}", new DocumentResponse(DocumentSources.Tenant, d.Key, d.Number, d.Title, d.Body, d.PublishedAt)));
    }

    private static async Task<IResult> RetireAsync(string key, HttpContext http, TermsService terms, CancellationToken cancellationToken)
    {
        var result = await terms.RetireAsync(key, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static IQueryable<AcceptanceRow> Rows(IdentityDbContext context) =>
        context.DocumentAcceptances.AsNoTracking()
            .Join(context.Principals.AsNoTracking(), a => a.PrincipalId, p => p.Id, (a, p) => new AcceptanceRow
            {
                Id = a.Id,
                PrincipalId = a.PrincipalId,
                LoginId = p.LoginId,
                DisplayName = p.DisplayName,
                Source = a.Source,
                DocumentKey = a.DocumentKey,
                Version = a.Version,
                AcceptedAt = a.AcceptedAt,
            });

    private static async Task<IResult> AcceptancesAsync(HttpContext http, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await Rows(context).ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page, http, query);
    }

    /// <summary>The acceptances as CSV (TF-028 AC2): the list's filters and sort apply; one row per acceptance.</summary>
    private static async Task<IResult> ExportAsync(HttpContext http, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var rows = await Rows(context).ApplyListQuery(parsed.Value, Fields).Take(ExportMaxRows).ToListAsync(cancellationToken);
        var csv = new StringBuilder("login_id,display_name,source,document,version,accepted_at\r\n");
        foreach (var row in rows)
        {
            csv.Append(Cell(row.LoginId)).Append(',').Append(Cell(row.DisplayName)).Append(',').Append(row.Source).Append(',').Append(Cell(row.DocumentKey)).Append(',')
                .Append(row.Version.ToString(CultureInfo.InvariantCulture)).Append(',').Append(row.AcceptedAt.ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return TypedResults.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "acceptances.csv");
    }

    /// <summary>A CSV cell: quoted when it holds a comma, a quote or a line break; a leading formula character is neutralised for spreadsheets.</summary>
    internal static string Cell(string value)
    {
        var text = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
