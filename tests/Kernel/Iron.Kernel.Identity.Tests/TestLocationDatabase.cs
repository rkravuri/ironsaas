using System.Net;
using System.Text;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Writes a small location database in the MaxMind DB format (version 2.0 of its specification), as the bundled DB-IP
/// one is laid out: a map with <c>city.names.en</c> and <c>country.names.en</c> for each network. IPv4 only, records of
/// 24 bits. It lets the tests read through the same reader the unit uses, without the real database's 130 MB.
/// </summary>
public static class TestLocationDatabase
{
    private static readonly byte[] MetadataMarker = [0xAB, 0xCD, 0xEF, .. Encoding.ASCII.GetBytes("MaxMind.com")];

    /// <summary>Writes the networks (address, prefix length, city, country) to a new file and answers its path.</summary>
    public static string Write(IReadOnlyList<(string Network, int Prefix, string? City, string Country)> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var data = new List<byte>();
        var offsets = new List<int>();
        foreach (var network in networks)
        {
            offsets.Add(data.Count);
            var record = new List<(string, object)> { ("country", Names(network.Country)) };
            if (network.City is not null)
            {
                record.Insert(0, ("city", Names(network.City)));
            }

            Encode(data, record);
        }

        // The tree: node 0 is the root; a child is a node's index, a network's data, or nothing.
        var nodes = new List<long?[]> { new long?[2] };
        var leaves = new Dictionary<(int Node, int Side), int>();
        for (var i = 0; i < networks.Count; i++)
        {
            var bytes = IPAddress.Parse(networks[i].Network).GetAddressBytes();
            var node = 0;
            for (var bit = 0; bit < networks[i].Prefix; bit++)
            {
                var side = (bytes[bit / 8] >> (7 - (bit % 8))) & 1;
                if (bit == networks[i].Prefix - 1)
                {
                    leaves[(node, side)] = offsets[i];
                    break;
                }

                if (nodes[node][side] is not { } next)
                {
                    nodes.Add(new long?[2]);
                    next = nodes.Count - 1;
                    nodes[node][side] = next;
                }

                node = (int)next;
            }
        }

        var count = nodes.Count;
        var tree = new List<byte>();
        for (var node = 0; node < count; node++)
        {
            for (var side = 0; side < 2; side++)
            {
                long value = leaves.TryGetValue((node, side), out var offset) ? count + 16 + offset : nodes[node][side] ?? count;
                tree.Add((byte)(value >> 16));
                tree.Add((byte)(value >> 8));
                tree.Add((byte)value);
            }
        }

        var metadata = new List<byte>();
        Encode(metadata, new List<(string, object)>
        {
            ("binary_format_major_version", (ushort)2),
            ("binary_format_minor_version", (ushort)0),
            ("build_epoch", (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("database_type", "Test-City"),
            ("description", new List<(string, object)> { ("en", "A location database for the tests") }),
            ("ip_version", (ushort)4),
            ("languages", new[] { "en" }),
            ("node_count", (uint)count),
            ("record_size", (ushort)24),
        });

        var path = Path.Combine(Path.GetTempPath(), $"locations-{Ids.New():N}.mmdb");
        using var file = File.Create(path);
        file.Write([.. tree]);
        file.Write(new byte[16]);
        file.Write([.. data]);
        file.Write(MetadataMarker);
        file.Write([.. metadata]);
        return path;
    }

    private static List<(string, object)> Names(string english) => [("names", new List<(string, object)> { ("en", english) })];

    private static void Encode(List<byte> output, object value)
    {
        switch (value)
        {
            case string text:
                var bytes = Encoding.UTF8.GetBytes(text);
                Control(output, 2, bytes.Length);
                output.AddRange(bytes);
                break;
            case ushort number:
                Unsigned(output, 5, number);
                break;
            case uint number:
                Unsigned(output, 6, number);
                break;
            case ulong number:
                Unsigned(output, 9, number);
                break;
            case string[] items:
                Control(output, 11, items.Length);
                foreach (var item in items)
                {
                    Encode(output, item);
                }

                break;
            case List<(string Key, object Value)> pairs:
                Control(output, 7, pairs.Count);
                foreach (var (key, item) in pairs)
                {
                    Encode(output, key);
                    Encode(output, item);
                }

                break;
            default:
                throw new ArgumentException($"No encoding for {value.GetType()}.", nameof(value));
        }
    }

    /// <summary>An unsigned number in as few big-endian bytes as it needs.</summary>
    private static void Unsigned(List<byte> output, int type, ulong number)
    {
        var bytes = new List<byte>();
        for (var rest = number; rest > 0; rest >>= 8)
        {
            bytes.Insert(0, (byte)rest);
        }

        Control(output, type, bytes.Count);
        output.AddRange(bytes);
    }

    /// <summary>
    /// The control byte: the type in its top three bits (or an extended type in the next byte) and the size, which from 29
    /// to 284 is 29 in the control byte and the rest in one byte after the type.
    /// </summary>
    private static void Control(List<byte> output, int type, int size)
    {
        if (size >= 285)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The test database keeps every value under 285 bytes or items.");
        }

        var small = size < 29 ? size : 29;
        if (type <= 7)
        {
            output.Add((byte)((type << 5) | small));
        }
        else
        {
            output.Add((byte)small);
            output.Add((byte)(type - 7));
        }

        if (size >= 29)
        {
            output.Add((byte)(size - 29));
        }
    }
}
