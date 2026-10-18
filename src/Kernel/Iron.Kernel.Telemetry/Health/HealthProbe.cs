namespace Iron.Kernel.Telemetry.Health;

/// <summary>
/// The <c>health</c> command of a host image: <c>dotnet Iron.Host.Api.dll health [url]</c> asks the readiness
/// endpoint and exits 0 when it answers 200. Container runtimes use it as the HEALTHCHECK, because the runtime image
/// has no shell and no curl. It runs before the host is built, so it is fast.
/// </summary>
public static class HealthProbe
{
    public const string Command = "health";
    public const string DefaultUrl = "http://localhost:8080/health/ready";

    public static bool IsCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], Command, StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        return await RunAsync(args, client).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(client);

        var url = args.Length > 1 ? args[1] : DefaultUrl;
        try
        {
            using var response = await client.GetAsync(new Uri(url, UriKind.RelativeOrAbsolute)).ConfigureAwait(false);
            var exitCode = response.IsSuccessStatusCode ? 0 : 1;
            await Console.Out.WriteLineAsync($"{url} -> {(int)response.StatusCode}").ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            await Console.Error.WriteLineAsync($"{url} -> {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
