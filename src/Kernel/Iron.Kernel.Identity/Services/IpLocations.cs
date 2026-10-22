using System.Net;
using System.Net.Sockets;
using MaxMind.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>Where an address is, roughly: a city and a country, in English, either of which may be unknown.</summary>
public sealed record IpLocation(string? City, string? Country);

/// <summary>
/// An approximate location for a client address (TF-022, ADR-0049), from a database bundled with the unit and never
/// fetched at runtime. A private, loopback or unknown address has none.
/// </summary>
public interface IIpLocations
{
    IpLocation? Find(IPAddress? address);
}

/// <summary>No database: every location is unknown. A host without one, and the tests that do not bring their own.</summary>
public sealed class NoIpLocations : IIpLocations
{
    public static readonly NoIpLocations Instance = new();

    public IpLocation? Find(IPAddress? address) => null;
}

/// <summary>
/// The DB-IP "IP to City Lite" database in its MMDB form (CC BY 4.0, docs/licences.md), read with MaxMind.Db: the API
/// image carries it at <c>Identity:Locations:DatabasePath</c>, written at build from a pinned month whose checksum the
/// build checks (ADR-0049). The file is mapped into memory, so a lookup touches a few pages of it, not the whole file. A
/// missing file means no locations, said once in the log; the sessions still work.
/// </summary>
public sealed class MmdbIpLocations : IIpLocations, IDisposable
{
    private readonly Reader? _reader;

    public MmdbIpLocations(string? path, ILogger<MmdbIpLocations> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            logger.LogWarning("No location database at {Path}: sessions show no location.", path ?? "(not configured)");
            return;
        }

        _reader = new Reader(path, FileAccessMode.MemoryMapped);
        logger.LogInformation("Location database {Path} opened: {Type}, built {Built:yyyy-MM-dd}.", path, _reader.Metadata.DatabaseType, _reader.Metadata.BuildDate);
    }

    public IpLocation? Find(IPAddress? address)
    {
        if (_reader is null || address is null || !ClientAddress.IsPublic(address))
        {
            return null;
        }

        var found = _reader.Find<Dictionary<string, object>>(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        if (found is null)
        {
            return null;
        }

        var city = EnglishName(found, "city");
        var country = EnglishName(found, "country");
        return city is null && country is null ? null : new IpLocation(city, country);
    }

    public void Dispose() => _reader?.Dispose();

    private static string? EnglishName(Dictionary<string, object> record, string part) =>
        record.TryGetValue(part, out var value) && value is Dictionary<string, object> place
        && place.TryGetValue("names", out var names) && names is Dictionary<string, object> words
        && words.TryGetValue("en", out var english) && english is string text && text.Length > 0
            ? Cut(text)
            : null;

    private static string Cut(string text) => text.Length > 100 ? text[..100] : text;
}

/// <summary>
/// The address of the client behind the ingress (ADR-0049). A unit answers through Traefik, so the connection comes from
/// a private address and the client's own is the last one the ingress added to <c>X-Forwarded-For</c>. A header from a
/// public peer is the client's own claim, and ignored.
/// </summary>
public static class ClientAddress
{
    public static IPAddress? Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var peer = http.Connection.RemoteIpAddress;
        if (peer is not null && IsPublic(peer))
        {
            return peer;
        }

        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (forwarded.Length > 0)
        {
            var last = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && IPAddress.TryParse(last, out var client))
            {
                return client;
            }
        }

        return peer;
    }

    /// <summary>Not loopback, not private, not link-local: an address a location database may know.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                _ => true,
            };
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6None));
    }
}
