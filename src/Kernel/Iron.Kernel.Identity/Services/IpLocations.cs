using System.Net;
using System.Net.Sockets;
using Iron.Kernel.Api.Http;
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
