namespace Iron.Kernel.Identity.Services;

/// <summary>Bound from the <c>Tokens</c> configuration section (TF-020, TF-023).</summary>
public sealed class TokenOptions
{
    public const string SectionName = "Tokens";

    /// <summary>Access tokens live this long (TF-020: 15 minutes).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>The audience every token names and every validation requires.</summary>
    public string Audience { get; set; } = "iron-api";

    /// <summary>The issuer; empty means <c>https://</c> and the unit's primary hostname.</summary>
    public string? Issuer { get; set; }

    /// <summary>The EC P-256 private key in PEM, inline. Takes precedence over the path.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>A file with the EC P-256 private key in PEM; written on first start when missing. T12 points it at the unit's secrets mount.</summary>
    public string? SigningKeyPath { get; set; }

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);
}
