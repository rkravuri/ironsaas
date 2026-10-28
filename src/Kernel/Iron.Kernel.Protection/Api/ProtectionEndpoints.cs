using System.Text.Json;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Protection.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Protection.Api;

/// <summary>A violation as the console lists it.</summary>
public sealed record CspViolationResponse(DateTimeOffset ReceivedAt, string Page, string Directive, string Blocked, string? Sample, string? Disposition);

public sealed record CspReportsResponse(IReadOnlyList<CspViolationResponse> Data);

/// <summary>
/// The Protection module's endpoints (ADR-0053): the report endpoint of the content security policy, which every page's
/// policy names, its latest reports for the console, and the login's challenge.
/// </summary>
internal static class ProtectionEndpoints
{
    /// <summary>Where browsers send the policy's reports, on every host: the policy names it in report-uri and report-to.</summary>
    public const string ReportPath = "/api/v1/csp-reports";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapApiGroup("csp-reports");
        reports.MapPost("/", ReportAsync)
            .AllowAnonymous()
            .RateLimited(RateLimitPolicies.CspReports)
            .LimitBodySize(ProtectionOptions.CspReportOptions.LargestBody)
            .Accepts<JsonElement>("application/csp-report", "application/reports+json", "application/json")
            .Produces(StatusCodes.Status204NoContent)
            .WithName("ReportCspViolation")
            .WithSummary("A browser reports a violation of the content security policy (report-uri or report-to form). Counted, sampled, and limited per address.");
        reports.MapGet("/", Latest)
            .RequirePermission(ProtectionPermissions.CspReportsRead)
            .Produces<CspReportsResponse>(StatusCodes.Status200OK)
            .WithName("ListCspViolations")
            .WithSummary("The latest reports this process received, newest first: the page, the rule it broke, what it blocked and a sample.");

        endpoints.MapGet("/auth/challenge", Challenge)
            .WithTags("auth")
            .AllowAnonymous()
            .Produces<LoginChallenge>(StatusCodes.Status200OK)
            .WithName("LoginChallenge")
            .WithSummary("A proof-of-work challenge for a sign-in that needs one after repeated failures (ALTCHA's form).");
    }

    private static async Task<IResult> ReportAsync(HttpContext http, CspReports reports, ProtectionOptions options, CancellationToken cancellationToken)
    {
        // The body is read here, not bound: browsers send it as application/csp-report or application/reports+json.
        var limit = options.CspReports.MaxBytes;
        if (http.Request.ContentLength > limit)
        {
            return ApiResults.Problem(Error.Validation(ProtectionErrors.CspReportTooLarge, $"A report is at most {limit} bytes."), http);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return ApiResults.Problem(Error.Validation(ProtectionErrors.CspReportTooLarge, $"A report is at most {limit} bytes."), http);
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return reports.Accept(document)
                ? TypedResults.NoContent()
                : ApiResults.Problem(Error.Validation(ProtectionErrors.CspReportInvalid, "This is no report of the content security policy."), http);
        }
        catch (JsonException)
        {
            return ApiResults.Problem(Error.Validation(ProtectionErrors.CspReportInvalid, "This is no report of the content security policy."), http);
        }
    }

    private static Ok<CspReportsResponse> Latest(CspReports reports) =>
        TypedResults.Ok(new CspReportsResponse([.. reports.Latest.Select(v => new CspViolationResponse(v.ReceivedAt, v.Page, v.Directive, v.Blocked, v.Sample, v.Disposition))]));

    private static async Task<Ok<LoginChallenge>> Challenge(HttpContext http, ILoginChallenges challenges, CancellationToken cancellationToken) =>
        TypedResults.Ok(await challenges.IssueAsync(ClientAddress.Of(http), cancellationToken));
}
