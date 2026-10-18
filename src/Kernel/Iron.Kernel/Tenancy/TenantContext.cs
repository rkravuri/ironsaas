namespace Iron.Kernel;

/// <summary>
/// The one tenant a running unit serves (TF-003). Built once at start from configuration and registered as a
/// singleton; it never changes while the process runs. The control plane uses <see cref="TenantId.ControlPlane"/>.
/// </summary>
public sealed class TenantContext
{
    public TenantContext(TenantId id, IReadOnlyList<string> hostnames)
    {
        ArgumentNullException.ThrowIfNull(hostnames);
        if (id.IsEmpty)
        {
            throw new ArgumentException("The tenant identifier is empty.", nameof(id));
        }

        var normalised = new List<string>();
        foreach (var hostname in hostnames)
        {
            var host = NormaliseHost(hostname);
            if (host.Length == 0)
            {
                throw new ArgumentException("A hostname is empty.", nameof(hostnames));
            }

            if (!normalised.Contains(host, StringComparer.Ordinal))
            {
                normalised.Add(host);
            }
        }

        if (normalised.Count == 0)
        {
            throw new ArgumentException("A tenant unit needs at least one hostname.", nameof(hostnames));
        }

        Id = id;
        Hostnames = normalised;
    }

    public TenantId Id { get; }

    /// <summary>The hostnames this unit answers to, lowercase and without ports. The first one is the primary.</summary>
    public IReadOnlyList<string> Hostnames { get; }

    public string PrimaryHostname => Hostnames[0];

    public bool IsControlPlane => Id == TenantId.ControlPlane;

    /// <summary>True when the request host (for example <c>Acme.localhost:443</c>) is one of this unit's hostnames.</summary>
    public bool ServesHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return Hostnames.Contains(NormaliseHost(host), StringComparer.Ordinal);
    }

    /// <summary>Lowercases a host and removes a trailing port, so <c>Acme.localhost:443</c> becomes <c>acme.localhost</c>.</summary>
    public static string NormaliseHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var value = host.Trim();
        if (!value.StartsWith('['))
        {
            var colon = value.LastIndexOf(':');
            if (colon >= 0)
            {
                value = value[..colon];
            }
        }

        return value.ToLowerInvariant();
    }
}
