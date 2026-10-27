using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Api.Http;

/// <summary>
/// The address of the client behind the ingress (ADR-0049). A unit answers through Traefik, so the connection comes from
/// a private address and the client's own is the last one the ingress added to <c>X-Forwarded-For</c>. A header from a
/// public peer is the client's own claim, and ignored. Identity places a session by it; the rate limits and the login
/// challenge count per address by it (ADR-0053).
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
