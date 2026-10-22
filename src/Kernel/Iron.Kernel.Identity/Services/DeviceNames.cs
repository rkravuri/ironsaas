namespace Iron.Kernel.Identity.Services;

/// <summary>A session's device in words a person recognises (TF-022): the browser and the platform it runs on.</summary>
public sealed record DeviceName(string? Browser, string? Platform);

/// <summary>
/// Reads a browser and a platform from a user agent (TF-022, ADR-0049), enough to say "Chrome on Windows". No library:
/// the shell needs a name a person recognises, not a full parse, and a user agent the reader does not know is named by
/// nothing rather than guessed. The order matters, since browsers name each other: Edge and Opera say Chrome, Chrome
/// says Safari.
/// </summary>
public static class DeviceNames
{
    public const int MaxLength = 40;

    public static DeviceName From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new DeviceName(null, null);
        }

        return new DeviceName(Browser(userAgent), Platform(userAgent));
    }

    private static string? Browser(string agent)
    {
        if (Has(agent, "Edg/") || Has(agent, "EdgA/") || Has(agent, "EdgiOS/"))
        {
            return "Edge";
        }

        if (Has(agent, "OPR/") || Has(agent, "Opera"))
        {
            return "Opera";
        }

        if (Has(agent, "Firefox/") || Has(agent, "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(agent, "Chrome/") || Has(agent, "CriOS/") || Has(agent, "Chromium/"))
        {
            return "Chrome";
        }

        if (Has(agent, "Safari/") && Has(agent, "Version/"))
        {
            return "Safari";
        }

        return null;
    }

    private static string? Platform(string agent)
    {
        if (Has(agent, "Windows"))
        {
            return "Windows";
        }

        if (Has(agent, "Android"))
        {
            return "Android";
        }

        if (Has(agent, "iPhone") || Has(agent, "iPod"))
        {
            return "iOS";
        }

        if (Has(agent, "iPad"))
        {
            return "iPadOS";
        }

        if (Has(agent, "CrOS"))
        {
            return "ChromeOS";
        }

        if (Has(agent, "Macintosh") || Has(agent, "Mac OS X"))
        {
            return "macOS";
        }

        if (Has(agent, "Linux"))
        {
            return "Linux";
        }

        return null;
    }

    private static bool Has(string agent, string part) => agent.Contains(part, StringComparison.Ordinal);
}
