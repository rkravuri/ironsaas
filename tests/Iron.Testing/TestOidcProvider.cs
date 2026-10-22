using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Iron.Testing;

/// <summary>
/// The single sign-on test provider (navikt's mock-oauth2-server, ADR-0048) for a test project: the run's one under make
/// test (<see cref="SharedAddressVariable"/>), or one of the project's own when it runs alone. Any first path segment of
/// its address is an issuer of its own with its own keys, so a test names an issuer of its own and meets no other test's.
/// Its login page takes the claims of each sign-in, which <see cref="SignInAsync"/> fills as a person would.
/// </summary>
public sealed class TestOidcProvider : IAsyncDisposable
{
    public const string Image = "ghcr.io/navikt/mock-oauth2-server:6.0.4";

    /// <summary>The run's provider, as make test started it: its address, http://127.0.0.1:port.</summary>
    public const string SharedAddressVariable = "IRON_TEST_OIDC";

    private readonly IContainer? _container;

    private TestOidcProvider(IContainer? container, string address)
    {
        _container = container;
        Address = address.TrimEnd('/');
    }

    public string Address { get; }

    public static async Task<TestOidcProvider> StartAsync(CancellationToken cancellationToken)
    {
        var address = Environment.GetEnvironmentVariable(SharedAddressVariable);
        if (!string.IsNullOrWhiteSpace(address))
        {
            return new TestOidcProvider(null, address);
        }

        var container = new ContainerBuilder(Image)
            .WithEnvironment("JSON_CONFIG", """{"interactiveLogin": true}""")
            .WithEnvironment("LOG_LEVEL", "WARN")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/isalive")))
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        return new TestOidcProvider(container, $"http://127.0.0.1:{container.GetMappedPublicPort(8080)}");
    }

    /// <summary>An issuer's address: the provider's address and the issuer's name.</summary>
    public string Issuer(string name) => $"{Address}/{name}";

    /// <summary>An issuer's discovery document.</summary>
    public string DiscoveryUrl(string name) => $"{Issuer(name)}/.well-known/openid-configuration";

    /// <summary>
    /// Signs in at the provider as a person would on its login page: the authorization URL the unit gave, a user name
    /// (the token's <c>sub</c>) and the claims of this sign-in. Answers where the provider sends the browser back: the
    /// unit's redirect URI with the code and the state, or with an error.
    /// </summary>
    public static async Task<Uri> SignInAsync(Uri authorizationUrl, string subject, IReadOnlyDictionary<string, object?> claims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = subject,
            ["claims"] = JsonSerializer.Serialize(claims),
        });
        using var response = await http.PostAsync(authorizationUrl, form, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || response.Headers.Location is null)
        {
            throw new InvalidOperationException($"The test provider did not send the browser back: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        return response.Headers.Location;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
