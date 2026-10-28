using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Security;

/// <summary>Settings of the web surface defaults. Bound from the <c>Security</c> configuration section; hosts may override in code.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public CorsSettings Cors { get; set; } = new();

    public HeaderSettings Headers { get; set; } = new();

    public CookieSettings Cookies { get; set; } = new();

    public sealed class CorsSettings
    {
        /// <summary>
        /// Origins that may call the API from a browser on every tenant, set by the operator. Empty means same-origin only
        /// (the shell is same-origin); a tenant adds its own in its settings, and the Protection module applies both (ADR-0053).
        /// </summary>
        public string[] AllowedOrigins { get; set; } = [];
    }

    public sealed class HeaderSettings
    {
        /// <summary>Where browsers report violations of the policy (TF-070 AC3, ADR-0053): the Protection module's endpoint on every host.</summary>
        public const string ReportPath = "/api/v1/csp-reports";

        /// <summary>The name the policy's <c>report-to</c> gives the endpoint in <c>Reporting-Endpoints</c>.</summary>
        public const string ReportGroup = "csp";

        /// <summary>
        /// No inline scripts or styles anywhere (TF-070), and every violation reported in both forms browsers know:
        /// <c>report-to</c> and the older <c>report-uri</c>, with a sample of what was blocked. The shell's nginx sends its
        /// own copy for its pages, with a nonce for the style elements its libraries make (ADR-0053).
        /// </summary>
        public string ContentSecurityPolicy { get; set; } =
            "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; "
            + "img-src 'self' data:; font-src 'self'; style-src 'self' 'report-sample'; script-src 'self' 'report-sample'; connect-src 'self'; "
            + $"report-uri {ReportPath}; report-to {ReportGroup}";

        public string PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";

        /// <summary>Sent on every response; a browser takes it only from a response that came over https.</summary>
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
