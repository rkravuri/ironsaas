using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.History;

namespace Iron.Kernel.Compliance.Services;

/// <summary>Who asked for a pack, when, for which tenant and period, and when it was made: what heads every document.</summary>
public sealed record EvidencePackHeading(string Tenant, EvidencePeriod Period, Guid RequestedById, string RequesterName, DateTimeOffset RequestedAt, DateTimeOffset MadeAt)
{
    /// <summary>Since when the audit trail keeps field-level history (<c>IRecordHistory.KeptSinceAsync</c>); null when always.</summary>
    public DateTimeOffset? FieldHistorySince { get; init; }

    /// <summary>
    /// The period starts before field-level history began (at T8's review): the access list, rebuilt from that history,
    /// is reliable only from that time, and says so at its top.
    /// </summary>
    public bool StartsBeforeFieldHistory => FieldHistorySince is { } since && Period.From < since;
}

/// <summary>A pack as written: the ZIP's bytes, its SHA-256, and its manifest's.</summary>
public sealed record WrittenPack(byte[] Zip, string Sha256, string ManifestSha256, IReadOnlyList<PackFile> Files);

public sealed record PackFile(string Path, string Sha256, long Bytes);

/// <summary>
/// Writes an evidence pack (TF-067 AC1, ADR-0052): for each of the five documents, in its numbered folder, one readable
/// page and one CSV per table, each starting with the tenant and the period; then <c>manifest.json</c> with every file's
/// SHA-256, the requester, the times, and the audit chain's head with its anchor; all in one ZIP. The same tables give
/// the same files, and the ZIP's entries carry a fixed time, so an auditor can recompute every hash the manifest lists.
/// </summary>
public static class EvidencePackWriter
{
    public const string Format = "iron-evidence-pack-v1";
    public const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static WrittenPack Write(EvidencePackHeading heading, IReadOnlyList<EvidenceTable> tables, EvidenceChainHead? head)
    {
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(tables);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var document in EvidenceDocuments.All)
        {
            var folder = EvidenceDocuments.FolderOf(document);
            var mine = tables.Where(t => t.Document == document).ToList();
            files[$"{folder}/{document}.html"] = Encoding.UTF8.GetBytes(Page(heading, document, mine, head));
            foreach (var table in mine)
            {
                files[$"{folder}/{table.Name}.csv"] = Encoding.UTF8.GetBytes(Csv(heading, document, table));
            }
        }

        var listed = files.Select(f => new PackFile(f.Key, Sha256(f.Value), f.Value.LongLength)).ToList();
        var manifest = new
        {
            Format,
            heading.Tenant,
            Period = new { From = CanonicalJson.Time(heading.Period.From), To = CanonicalJson.Time(heading.Period.To) },
            RequestedBy = new { Id = heading.RequestedById, Name = heading.RequesterName },
            RequestedAt = CanonicalJson.Time(heading.RequestedAt),
            MadeAt = CanonicalJson.Time(heading.MadeAt),
            FieldHistorySince = heading.FieldHistorySince is { } since ? CanonicalJson.Time(since) : null,
            ChainHead = head is null ? null : new
            {
                head.Segment,
                head.Position,
                head.Hash,
                SealedAt = CanonicalJson.Time(head.SealedAt),
                Anchor = head.Anchor is not { } anchor ? null : new { anchor.Id, anchor.Segment, anchor.Position, anchor.Hash, At = CanonicalJson.Time(anchor.At), ReceivedAt = CanonicalJson.Time(anchor.ReceivedAt) },
            },
            Files = listed.Select(f => new { f.Path, f.Sha256, f.Bytes }),
        };
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json) + "\n");
        files[ManifestName] = manifestBytes;

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        var zipped = buffer.ToArray();
        return new WrittenPack(zipped, Sha256(zipped), Sha256(manifestBytes), listed);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A CSV field: quoted when it holds a comma, a quote or a line break; a formula's first character defused, as the audit export does.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>
    /// What the access list says at its top when the period starts before field-level history began (at T8's review): its
    /// state at the period's end and its changes in the period are rebuilt from that history, so they are reliable only
    /// from that time. Null for the other documents, and when the history covers the whole period.
    /// </summary>
    public static string? FieldHistoryNotice(EvidencePackHeading heading, string document)
    {
        ArgumentNullException.ThrowIfNull(heading);
        if (document != EvidenceDocuments.AccessList || !heading.StartsBeforeFieldHistory)
        {
            return null;
        }

        var since = CanonicalJson.Time(heading.FieldHistorySince!.Value);
        return $"This period starts before the audit trail kept field-level history, which began at {since} UTC. The access list is rebuilt from that history, so it is reliable from {since} UTC: a grant, removal or change made before then is neither listed nor undone.";
    }

    private static string Csv(EvidencePackHeading heading, string document, EvidenceTable table)
    {
        var csv = new StringBuilder();
        csv.Append("# ").Append(EvidenceDocuments.TitleOf(document)).Append(": ").Append(table.Title).Append('\n');
        csv.Append("# Tenant: ").Append(heading.Tenant).Append(". Period: ").Append(CanonicalJson.Time(heading.Period.From)).Append(" up to ").Append(CanonicalJson.Time(heading.Period.To)).Append(" UTC.\n");
        if (FieldHistoryNotice(heading, document) is { } notice)
        {
            csv.Append("# ").Append(notice).Append('\n');
        }

        if (table.Note is { } note)
        {
            csv.Append("# ").Append(note.Replace('\n', ' ')).Append('\n');
        }

        csv.Append(string.Join(',', table.Columns.Select(c => c.Key))).Append('\n');
        foreach (var row in table.Rows)
        {
            csv.Append(string.Join(',', row.Select(Field))).Append('\n');
        }

        return csv.ToString();
    }

    private static string Page(EvidencePackHeading heading, string document, IReadOnlyList<EvidenceTable> tables, EvidenceChainHead? head)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        var title = EvidenceDocuments.TitleOf(document);
        var page = new StringBuilder();
        page.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append(": ").Append(E(heading.Tenant)).Append("</title>");
        page.Append("<style>body{font-family:system-ui,sans-serif;margin:2rem auto;max-width:80rem;padding:0 1rem;color:#1b1f24;background:#fff;line-height:1.45}h1{font-size:1.5rem}h2{font-size:1.15rem;margin-top:2rem}table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}th,td{border:1px solid #c9ced6;padding:.3rem .45rem;text-align:left;vertical-align:top;font-size:.9rem}th{background:#eef1f5}.note{color:#4a525e}.caution{border-left:4px solid #8a5a00;background:#fff6e5;padding:.6rem .8rem}dl{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem}dt{font-weight:600}</style></head><body>");
        page.Append("<h1>").Append(E(title)).Append("</h1>");
        if (FieldHistoryNotice(heading, document) is { } notice)
        {
            page.Append("<p class=\"caution\" role=\"note\" data-field-history-since=\"").Append(E(CanonicalJson.Time(heading.FieldHistorySince!.Value))).Append("\"><strong>").Append(E(notice)).Append("</strong></p>");
        }

        page.Append("<dl>");
        page.Append("<dt>Tenant</dt><dd>").Append(E(heading.Tenant)).Append("</dd>");
        page.Append("<dt>Period</dt><dd>").Append(E(CanonicalJson.Time(heading.Period.From))).Append(" up to ").Append(E(CanonicalJson.Time(heading.Period.To))).Append(" (UTC)</dd>");
        page.Append("<dt>Requested by</dt><dd>").Append(E(heading.RequesterName)).Append(", ").Append(E(CanonicalJson.Time(heading.RequestedAt))).Append(" (UTC)</dd>");
        page.Append("<dt>Made</dt><dd>").Append(E(CanonicalJson.Time(heading.MadeAt))).Append(" (UTC)</dd>");
        if (head is not null)
        {
            page.Append("<dt>Audit chain head</dt><dd>segment ").Append(head.Segment.ToString(CultureInfo.InvariantCulture)).Append(", position ").Append(head.Position.ToString(CultureInfo.InvariantCulture)).Append(", hash <code>").Append(E(head.Hash)).Append("</code>");
            page.Append(head.Anchor is { Id: { } anchor } ? $", anchor {E(anchor.ToString("D"))}" : ", not anchored").Append("</dd>");
        }

        page.Append("</dl><p class=\"note\">Every file of this pack and its SHA-256 are listed in manifest.json at the pack's root.</p>");
        if (tables.Count == 0)
        {
            page.Append("<p>Nothing to show for this period.</p>");
        }

        foreach (var table in tables)
        {
            page.Append("<h2>").Append(E(table.Title)).Append("</h2>");
            if (table.Note is { } note)
            {
                page.Append("<p class=\"note\">").Append(E(note)).Append("</p>");
            }

            page.Append("<table><thead><tr>");
            foreach (var column in table.Columns)
            {
                page.Append("<th scope=\"col\">").Append(E(column.Header)).Append("</th>");
            }

            page.Append("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                page.Append("<tr>");
                foreach (var cell in row)
                {
                    page.Append("<td>").Append(E(cell)).Append("</td>");
                }

                page.Append("</tr>");
            }

            page.Append("</tbody></table>");
        }

        return page.Append("</body></html>\n").ToString();
    }
}
