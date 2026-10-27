using System.Collections.Concurrent;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Security;
using Iron.Kernel.Settings.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Protection.Tests;

/// <summary>
/// A host with the Protection module and a few endpoints under its limits, served in memory. The tenant's settings, the
/// principal (from the <c>X-Test-Principal</c> header), the permission evaluator (everyone may) and the audit trail stand
/// in. One host per test: each has its own counters.
/// </summary>
public sealed class ProtectionHost : IAsyncDisposable
{
    public const string PrincipalHeader = "X-Test-Principal";

    private readonly WebApplication _app;

    private ProtectionHost(WebApplication app, StandInSettings settings, RecordingAudit audit)
    {
        _app = app;
        Settings = settings;
        Audit = audit;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public StandInSettings Settings { get; }

    public RecordingAudit Audit { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ProtectionHost> StartAsync(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration ?? new Dictionary<string, string?>());
        var settings = new StandInSettings();
        var audit = new RecordingAudit();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddIronApi();
        builder.Services.AddIronSecurity(builder.Configuration);
        new ProtectionModule().AddServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<ISettings>(settings);
        builder.Services.AddSingleton<IAuditWriter>(audit);
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.TryAddSingleton<IPermissionEvaluator, EveryoneMay>();

        var app = builder.Build();
        app.UseIronApi();
        app.UseIronSecurity();
        app.UseIronProtection();
        new ProtectionModule().MapEndpoints(app);
        app.MapPost("/auth/login", () => TypedResults.Ok()).AllowAnonymous().RateLimited(RateLimitPolicies.Login);
        app.MapGet("/api/v1/things", () => TypedResults.Ok()).AllowAnonymous();
        app.MapPost("/api/v1/things/export", () => TypedResults.Ok()).AllowAnonymous().RateLimited(RateLimitPolicies.Exports);
        app.MapPost("/api/v1/people/{id}/recovery-link", (string id) => TypedResults.Ok(id)).AllowAnonymous().RateLimited(RateLimitPolicies.Recovery, "id");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ProtectionHost(app, settings, audit);
    }

    /// <summary>A request from an address behind the ingress, and as a principal when one is given.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string address = "203.0.113.7", Guid? principal = null, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Forwarded-For", address);
        if (principal is { } id)
        {
            request.Headers.Add(PrincipalHeader, id.ToString("D"));
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}

/// <summary>The tenant's settings as the test sets them; anything else answers its default.</summary>
public sealed class StandInSettings : ISettings
{
    private readonly ConcurrentDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public void Set<T>(Setting<T> setting, T value) => _values[setting.Key] = setting.ToJson(value);

    public Task<T> GetAsync<T>(Setting<T> setting, SettingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(setting.Key, out var value) ? setting.Read(value) : setting.DefaultValue);

    public Task<ResolvedSetting> ResolveAsync(SettingDefinition setting, SettingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedSetting(setting.Key, _values.TryGetValue(setting.Key, out var value) ? value : setting.Default, SettingLevel.Default, null, null, null));
}

/// <summary>The audit events written, in order.</summary>
public sealed class RecordingAudit : IAuditWriter
{
    public ConcurrentQueue<AuditEvent> Events { get; } = new();

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>The principal named in the request's test header; anonymous without it.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public Guid Id => accessor.HttpContext?.Request.Headers[ProtectionHost.PrincipalHeader].ToString() is { Length: > 0 } text && Guid.TryParse(text, out var id) ? id : Guid.Empty;
}

/// <summary>Every principal holds every permission here: the permissions have their own tests.</summary>
public sealed class EveryoneMay : IPermissionEvaluator
{
    public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AccessDecision(true, AccessDecision.Granted));
}
