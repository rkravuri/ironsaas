using System.Net;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Fleet.Services.Compose;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>The Compose runtime without Docker: what it renders, what it runs, and how it reads the ingress.</summary>
public sealed class ComposeUnitRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iron-units-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingProcesses _processes = new();
    private readonly FleetOptions _options;

    public ComposeUnitRuntimeTests()
    {
        _options = new FleetOptions();
        _options.Units.Runtime = "compose";
        _options.Units.ComposeDirectory = Path.GetRelativePath(Directory.GetCurrentDirectory(), _directory);
        _options.Units.IngressUrl = "http://ingress.test";
        _options.Units.ImageTag = "1.2.3";
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Proves("ADR-0019")]
    public void TheTemplateIsEmbeddedAndEveryVariableItUsesIsRendered()
    {
        var template = ComposeUnitRuntime.Template();
        var variables = Runtime(HttpStatusCode.OK).Variables(Unit());

        var used = System.Text.RegularExpressions.Regex.Matches(template, @"\$\{([A-Z_]+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(used);
        Assert.All(used, name => Assert.True(variables.ContainsKey(name), $"The template uses {name} but the fleet does not render it."));
        Assert.Equal("Host(`acme.localhost`) || Host(`acme.example.com`)", variables["HOST_RULE"]);
        Assert.Equal("acme.localhost,acme.example.com", variables["HOSTNAMES"]);
        Assert.Equal("1", variables["API_CPUS"]);
        Assert.Equal("1024", variables["API_MEMORY_MB"]);
        Assert.Equal("unit-token", variables["SECRETS_TOKEN"]);
    }

    [Fact]
    [Proves("ADR-0019")]
    public async Task ApplyRendersTheProjectAndRunsComposeUp()
    {
        var runtime = Runtime(HttpStatusCode.OK);

        await runtime.ApplyAsync(Unit(), TestContext.Current.CancellationToken);

        var directory = Path.Combine(_directory, "acme");
        Assert.True(Path.IsPathRooted(directory));
        Assert.True(File.Exists(Path.Combine(directory, "compose.yaml")));
        var env = await File.ReadAllTextAsync(Path.Combine(directory, ".env"), TestContext.Current.CancellationToken);
        Assert.Contains("TENANT=acme\n", env, StringComparison.Ordinal);
        Assert.Contains("IMAGE_TAG=1.2.3\n", env, StringComparison.Ordinal);
        var call = Assert.Single(_processes.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal(["compose", "--project-name", "tenant-acme", "--file", Path.Combine(directory, "compose.yaml"), "--env-file", Path.Combine(directory, ".env"), "up", "--detach", "--wait", "--remove-orphans"], call.Arguments);
    }

    [Fact]
    [Proves("ADR-0019")]
    public async Task ACommandRunsTheApiImageOnThePlatformNetworkWithTheEnvironmentAndCleansUp()
    {
        var runtime = Runtime(HttpStatusCode.OK);

        var result = await runtime.RunCommandAsync(Unit(), ["migrate"], TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var call = Assert.Single(_processes.Calls);
        Assert.Equal("run", call.Arguments[0]);
        Assert.Contains("--network", call.Arguments);
        Assert.Equal("foundation", call.Arguments[call.Arguments.IndexOf("--network") + 1]);
        Assert.Equal("foundation/iron-api:1.2.3", call.Arguments[^2]);
        Assert.Equal("migrate", call.Arguments[^1]);
        var envFile = call.Arguments[call.Arguments.IndexOf("--env-file") + 1];
        Assert.Contains("Secrets__Token=unit-token\n", _processes.EnvFiles[envFile], StringComparison.Ordinal);
        Assert.False(File.Exists(envFile), "the env file with the token must not stay on disk");
    }

    [Fact]
    [Untraced("No ADR or criterion states that a failed compose up surfaces Compose's own output; TF-002 AC2's reporting of a failed step is proved at the job level.")]
    public async Task FailingComposeUpIsReportedWithItsOutput()
    {
        _processes.ExitCode = 1;
        _processes.Output = "service api: container unhealthy";
        var runtime = Runtime(HttpStatusCode.OK);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ApplyAsync(Unit(), TestContext.Current.CancellationToken));

        Assert.Contains("container unhealthy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0019")]
    public async Task ReadinessGoesThroughTheIngressWithTheTenantHostname()
    {
        var ready = Runtime(HttpStatusCode.OK);
        Assert.Null(await ready.CheckReadyAsync(Unit(), TestContext.Current.CancellationToken));
        Assert.Equal("acme.localhost", ready.LastHost);

        var notYet = Runtime(HttpStatusCode.ServiceUnavailable);
        var problem = await notYet.CheckReadyAsync(Unit(), TestContext.Current.CancellationToken);
        Assert.Contains("503", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0019")]
    public async Task RemoveRunsComposeDown()
    {
        var runtime = Runtime(HttpStatusCode.OK);

        await runtime.RemoveAsync(Unit(), TestContext.Current.CancellationToken);

        Assert.Contains("down", Assert.Single(_processes.Calls).Arguments);
    }

    [Fact]
    [Untraced("The env file's refusal of a value with a line break is an encoding guard that no ADR or criterion states.")]
    public void AnEnvFileRefusesLineBreaks()
    {
        Assert.Throws<InvalidOperationException>(() => ComposeUnitRuntime.EnvFile(new Dictionary<string, string> { ["KEY"] = "line\nbreak" }));
        Assert.Equal("A=1\nB=x=y;z\n", ComposeUnitRuntime.EnvFile(new Dictionary<string, string> { ["B"] = "x=y;z", ["A"] = "1" }));
    }

    private TestRuntime Runtime(HttpStatusCode ingressAnswer) => new(_processes, _options, ingressAnswer);

    private static UnitDefinition Unit() => new(
        new TenantRecord(Ids.New(), "acme", "Acme", ["acme.localhost", "acme.example.com"], "local", "standard", SizeClass.Medium, TenantStatus.Provisioning, "admin@acme.test", null, "acme", "acme", DateTimeOffset.UnixEpoch, null),
        "1.2.3",
        SizeClasses.LimitsOf(SizeClass.Medium),
        new Dictionary<string, string> { ["Secrets__Address"] = "http://openbao:8200", ["Secrets__Token"] = "unit-token", ["Secrets__Mount"] = "secret", ["Secrets__Path"] = "tenants/acme", ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel-collector:4317" });

    private sealed class TestRuntime : IUnitRuntime
    {
        private readonly ComposeUnitRuntime _inner;
        private readonly IngressHandler _handler;

        public TestRuntime(RecordingProcesses processes, FleetOptions options, HttpStatusCode answer)
        {
            _handler = new IngressHandler(answer);
            _inner = new ComposeUnitRuntime(processes, new HttpClient(_handler), options, NullLogger<ComposeUnitRuntime>.Instance);
        }

        public string? LastHost => _handler.LastHost;

        public IReadOnlyDictionary<string, string> Variables(UnitDefinition unit) => _inner.Variables(unit);

        public Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken) => _inner.RunCommandAsync(unit, command, cancellationToken);

        public Task ApplyAsync(UnitDefinition unit, CancellationToken cancellationToken) => _inner.ApplyAsync(unit, cancellationToken);

        public Task<string?> CheckReadyAsync(UnitDefinition unit, CancellationToken cancellationToken) => _inner.CheckReadyAsync(unit, cancellationToken);

        public Task RemoveAsync(UnitDefinition unit, CancellationToken cancellationToken) => _inner.RemoveAsync(unit, cancellationToken);
    }

    private sealed class IngressHandler(HttpStatusCode answer) : HttpMessageHandler
    {
        public string? LastHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHost = request.Headers.Host;
            return Task.FromResult(new HttpResponseMessage(answer) { Content = new StringContent("{\"status\":\"" + (answer == HttpStatusCode.OK ? "healthy" : "unhealthy") + "\"}") });
        }
    }

    private sealed class RecordingProcesses : IProcessRunner
    {
        public List<(string FileName, List<string> Arguments)> Calls { get; } = [];

        public Dictionary<string, string> EnvFiles { get; } = [];

        public int ExitCode { get; set; }

        public string Output { get; set; } = "ok";

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var list = arguments.ToList();
            Calls.Add((fileName, list));
            var envFile = list.IndexOf("--env-file");
            if (envFile >= 0 && list[0] == "run")
            {
                EnvFiles[list[envFile + 1]] = File.ReadAllText(list[envFile + 1]);
            }

            return Task.FromResult(new ProcessResult(ExitCode, Output));
        }
    }
}
