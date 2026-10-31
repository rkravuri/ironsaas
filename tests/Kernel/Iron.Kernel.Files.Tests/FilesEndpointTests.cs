using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Files.Tests;

/// <summary>Upload, read, download and delete over HTTP, with the permissions in front.</summary>
[Collection(FilesCollection.Name)]
public class FilesEndpointTests(FilesFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("A file that travels over HTTP.");

    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = fixture.StorageConnectionString,
            ["Storage:Container"] = FilesFixture.Container,
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FilesModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    [Proves("TF-160 AC1", "ADR-0016")]
    public async Task TheAdministratorUploadsReadsDownloadsAndDeletesAFile()
    {
        var alice = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Content);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "travel.txt");
        form.Add(new StringContent("widget"), "record_type");
        using var upload = await Send(HttpMethod.Post, "/api/v1/files", alice, form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var created = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("travel.txt", created.RootElement.GetProperty("name").GetString());
        Assert.Equal("widget", created.RootElement.GetProperty("record_type").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Content)), created.RootElement.GetProperty("sha256").GetString());

        using var details = await Send(HttpMethod.Get, $"/api/v1/files/{id}", alice);
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);

        using var download = await Send(HttpMethod.Get, $"/api/v1/files/{id}/content", alice);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Content, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Content)), Assert.Single(download.Headers.GetValues("X-Checksum-SHA256")));
        Assert.Contains("travel.txt", download.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);

        using var delete = await Send(HttpMethod.Delete, $"/api/v1/files/{id}", alice);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var gone = await Send(HttpMethod.Get, $"/api/v1/files/{id}", alice);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    [Proves("ADR-0012")]
    public async Task AReaderCannotUploadAndNobodyGetsNothing()
    {
        var reader = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(reader, SystemRoles.ReadOnly, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var form = new MultipartFormDataContent { { new ByteArrayContent(Content), "file", "denied.txt" } };
        using var denied = await Send(HttpMethod.Post, "/api/v1/files", reader, form);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PermissionErrors.PermissionDenied, problem.RootElement.GetProperty("error_code").GetString());

        using var anonymous = await Send(HttpMethod.Get, $"/api/v1/files/{Ids.New()}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid? principal, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
