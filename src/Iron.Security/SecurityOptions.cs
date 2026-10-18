using Microsoft.AspNetCore.Http;

namespace Iron.Security;

/// <summary>Settings of the web surface defaults. Bound from the <c>Security</c> configuration section; hosts may override in code.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public CorsSettings Cors { get; set; } = new();

    public HeaderSettings Headers { get; set; } = new();

    public CookieSettings Cookies { get; set; } = new();

    public sealed class CorsSettings
    {
        /// <summary>Origins that may call the API from a browser. Empty means same-origin only (the shell is same-origin).</summary>
        public string[] AllowedOrigins { get; set; } = [];
    }

    public sealed class HeaderSettings
    {
        /// <summary>No inline scripts or styles anywhere (TF-070). The shell ships its own copy for its pages (T15).</summary>
        public string ContentSecurityPolicy { get; set; } =
            "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; "
            + "img-src 'self' data:; font-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'";

        public string PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";

        /// <summary>Sent on https responses only; browsers ignore it over http anyway.</summary>
        public bool EnableHsts { get; set; } = true;

        /// <summary>Paths that get no content security policy, for example the API browser in Development.</summary>
        public string[] CspExemptPathPrefixes { get; set; } = [];
    }

    public sealed class CookieSettings
    {
        /// <summary>Always on. Browsers treat localhost and *.localhost as secure, so the laptop works over http too.</summary>
        public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.Always;
    }
}
