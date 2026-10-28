using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Security.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Security.Tests;

/// <summary>The sample field of TF-071 AC2: a note with a title and a rich-text body, as the business documents will have.</summary>
public sealed record SampleNote(string Title, RichText? Body);

public sealed class SampleNoteValidator : AbstractValidator<SampleNote>
{
    public SampleNoteValidator()
    {
        RuleFor(n => n.Title).NotEmpty().MaximumLength(100);
        RuleFor(n => n.Body).MaximumLength(500);
    }
}

public sealed class SampleNoteWithoutLimitValidator : AbstractValidator<SampleNote>
{
    public SampleNoteWithoutLimitValidator()
    {
        RuleFor(n => n.Title).NotEmpty().MaximumLength(100);
    }
}

/// <summary>TF-071 AC2 (ADR-0053): rich text loses its scripts, its event handlers and its dangerous addresses, and keeps the rest.</summary>
public class RichTextTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [Proves("TF-071 AC2", "ADR-0053")]
    [InlineData("<p>Hello <script>alert(1)</script>world</p>", "script", "alert")]
    [InlineData("<img src=x onerror=alert(1)><p>after</p>", "img", "onerror")]
    [InlineData("<p onclick=\"steal()\">text</p>", "onclick", "steal")]
    [InlineData("<a href=\"javascript:alert(1)\">link</a>", "javascript", "alert")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">link</a>", "javascript", "alert")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">link</a>", "avascript", "alert")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">link</a>", "data:", "base64")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">link</a>", "vbscript", "msgbox")]
    [InlineData("<svg onload=alert(1)><circle r=\"1\"/></svg><p>after</p>", "svg", "onload")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe><p>after</p>", "iframe", "evil.example")]
    [InlineData("<style>body{display:none}</style><p>after</p>", "style", "display")]
    [InlineData("<p style=\"background:url(javascript:alert(1))\">x</p>", "style", "javascript")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"p\"><button>Go</button></form><p>after</p>", "form", "evil.example")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>", "onerror", "alert")]
    [InlineData("<object data=\"https://evil.example/x.swf\"></object><embed src=\"x.swf\"><p>after</p>", "object", "swf")]
    [InlineData("<p>x</p><!-- <script>alert(1)</script> -->", "script", "<!--")]
    public void ScriptsHandlersAndDangerousAddressesAreRemoved(string html, string gone, string alsoGone)
    {
        var clean = RichText.From(html).Html;

        Assert.DoesNotContain(gone, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(alsoGone, clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Proves("TF-071 AC2", "ADR-0053")]
    public void FormattingListsTablesQuotesCodeAndSafeLinksAreKept()
    {
        const string Html = "<h2>Terms</h2><p>Pay within <strong>30 days</strong> of the <em>invoice</em>.<br>Thank you.</p>"
            + "<ul><li>One</li><li>Two</li></ul><ol><li>First</li></ol><blockquote>Quoted</blockquote><pre><code>x = 1</code></pre>"
            + "<table><thead><tr><th>Item</th></tr></thead><tbody><tr><td colspan=\"2\">Cell</td></tr></tbody></table>"
            + "<p><a href=\"https://example.com/terms\" title=\"The terms\">the terms</a> or <a href=\"mailto:buyer@example.com\">write</a></p>";

        var clean = RichText.From(Html).Html;

        Assert.Equal(
            Html
                .Replace("<a href=\"https://example.com/terms\" title=\"The terms\">", "<a href=\"https://example.com/terms\" title=\"The terms\" rel=\"noopener noreferrer nofollow\">", StringComparison.Ordinal)
                .Replace("<a href=\"mailto:buyer@example.com\">", "<a href=\"mailto:buyer@example.com\" rel=\"noopener noreferrer nofollow\">", StringComparison.Ordinal),
            clean);
    }

    [Fact]
    [Proves("TF-071 AC2", "ADR-0053")]
    public void ATagOffTheListGoesButItsTextStays()
    {
        Assert.Equal("<p>Pasted text and more</p>", RichText.From("<div><p><span style=\"color:red\">Pasted</span> <font face=\"x\">text</font> and more</p></div>").Html);
    }

    [Fact]
    [Proves("TF-071 AC2", "TF-071 AC1", "ADR-0053")]
    public async Task ARequestsRichTextFieldIsCleanedAsItBindsAndRefusedOverItsLimitNamingTheField()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var cleaned = await client.PostAsJsonAsync(new Uri("/api/v1/notes", UriKind.Relative), new { title = "Note", body = "<p onmouseover=\"x()\">Kept <strong>bold</strong><script>alert(1)</script></p>" }, Cancel);
        using var tooLong = await client.PostAsJsonAsync(new Uri("/api/v1/notes", UriKind.Relative), new { title = "Note", body = "<p>" + new string('x', 501) + "</p>" }, Cancel);
        using var hostile = await client.PostAsJsonAsync(new Uri("/api/v1/notes", UriKind.Relative), new { title = "Note", body = 42 }, Cancel);
        using var problem = JsonDocument.Parse(await tooLong.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(HttpStatusCode.OK, cleaned.StatusCode);
        Assert.Equal("<p>Kept <strong>bold</strong></p>", await cleaned.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var error = problem.RootElement.GetProperty("errors")[0];
        Assert.Equal(("body", "maximum_length"), (error.GetProperty("field").GetString(), error.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, hostile.StatusCode);
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public void ARichTextFieldWithoutAMaximumLengthIsCaughtByTheRequestLimitCheck()
    {
        var without = RequestLimitCheck.Check(typeof(SampleNote), new SampleNoteWithoutLimitValidator(), _ => null);
        var with = RequestLimitCheck.Check(typeof(SampleNote), new SampleNoteValidator(), _ => null);

        Assert.Equal(["body is RichText text without a maximum length. Add .MaximumLength(n)."], without);
        Assert.Empty(with);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddScoped<IValidator<SampleNote>, SampleNoteValidator>();
        var app = builder.Build();
        app.UseIronApi();
        app.MapPost("/api/v1/notes", (SampleNote note) => Results.Text(note.Body?.Html ?? string.Empty)).AllowAnonymous().Validate<SampleNote>();
        await app.StartAsync(Cancel);
        return app;
    }
}
