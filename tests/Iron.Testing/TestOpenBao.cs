using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Iron.Testing;

/// <summary>
/// OpenBao in development mode for a test project, with a KV version 2 mount of the project's own (<see cref="Mount"/>):
/// the run's one store under make test (<see cref="SharedAddressVariable"/> and <see cref="SharedTokenVariable"/>), or
/// one of the project's own when it runs alone. Policies and tokens live in the whole store; the fleet's are its tenants'.
/// </summary>
public sealed class TestOpenBao : IAsyncDisposable
{
    public const string Image = "openbao/openbao:2.7.1";

    /// <summary>The run's store, as make test started it: its address and its root token.</summary>
    public const string SharedAddressVariable = "IRON_TEST_OPENBAO";

    public const string SharedTokenVariable = "IRON_TEST_OPENBAO_TOKEN";

    private const string OwnRootToken = "iron-test-root-token";

    private readonly IContainer? _container;

    private TestOpenBao(IContainer? container, string address, string rootToken, string mount)
    {
        _container = container;
        Address = address;
        RootToken = rootToken;
        Mount = mount;
    }

    public string Address { get; }

    public string RootToken { get; }

    /// <summary>The project's own KV version 2 mount, named after the project.</summary>
    public string Mount { get; }

    /// <summary>The project's own transit engine, for its keys (ADR-0054): <c>&lt;project&gt;-transit</c>.</summary>
    public string TransitMount => $"{Mount}-transit";

    public static async Task<TestOpenBao> StartAsync(string project, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        IContainer? container = null;
        var address = Environment.GetEnvironmentVariable(SharedAddressVariable);
        var token = Environment.GetEnvironmentVariable(SharedTokenVariable);
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token))
        {
            container = new ContainerBuilder(Image)
                .WithCommand("server", "-dev", "-dev-root-token-id=" + OwnRootToken, "-dev-listen-address=0.0.0.0:8200")
                .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                    parameters.HostConfig.CapAdd = ["IPC_LOCK"];
                })
                .WithPortBinding(8200, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8200).ForPath("/v1/sys/health")))
                .Build();
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            address = $"http://{container.Hostname}:{container.GetMappedPublicPort(8200)}";
            token = OwnRootToken;
        }

        var store = new TestOpenBao(container, address, token, project);
        await store.EnableMountAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Enables the project's mount and waits until it answers, so the first write of a test finds it ready.</summary>
    private async Task EnableMountAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(Address.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        using (var enabled = await http.PostAsJsonAsync(new Uri($"v1/sys/mounts/{Mount}", UriKind.Relative), new { type = "kv", options = new Dictionary<string, string> { ["version"] = "2" } }, cancellationToken).ConfigureAwait(false))
        {
            if (!enabled.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The store at {Address} did not enable the mount '{Mount}': {(int)enabled.StatusCode} {await enabled.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
            }
        }

        using (var transit = await http.PostAsJsonAsync(new Uri($"v1/sys/mounts/{TransitMount}", UriKind.Relative), new { type = "transit" }, cancellationToken).ConfigureAwait(false))
        {
            if (!transit.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The store at {Address} did not enable the transit mount '{TransitMount}': {(int)transit.StatusCode} {await transit.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
            }
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var config = await http.GetAsync(new Uri($"v1/{Mount}/config", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (config.IsSuccessStatusCode)
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"The mount '{Mount}' at {Address} did not answer within five seconds.");
    }
}
