using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Compliance.Contracts;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// The snapshot's canonical form as the Audit README documents it (ADR-0051), recomputed by an implementation of its own
/// that shares no code with the platform's, as an auditor's would.
/// </summary>
public class SnapshotFormTests
{
    [Fact]
    [Proves("TF-063 AC2", "ADR-0051")]
    public void AnIndependentImplementationOfTheDocumentedFormRecomputesTheHash()
    {
        using var content = JsonDocument.Parse("""
            {"title":"Café \"Zoë\"\t","amount":12.50,"lines":[{"z":1,"a":1E2},{"b":null,"a":true}],"note":"line\nbreak\u0001","whole":-0.0}
            """);
        var snapshot = new RecordSnapshot("sample_document", Guid.Parse("01a14e12-f3a8-7155-b5b6-17c3abceae77"), 3, "A title nobody hashes", content.RootElement.Clone());

        var platform = SnapshotCanonicalForm.Write(snapshot);

        Assert.Equal(IndependentCanonical(snapshot), platform);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(IndependentCanonical(snapshot)))), SnapshotCanonicalForm.Hash(snapshot));
        Assert.StartsWith("""["iron-snapshot-v1","sample_document","01a14e12-f3a8-7155-b5b6-17c3abceae77",3,{"amount":12.5,""", platform, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-219 AC2", "ADR-0051")]
    public void TheSameContentForAnotherRecordOrAnotherVersionHashesDifferently()
    {
        var content = JsonSerializer.SerializeToElement(new { title = "Same words" });
        var signed = new RecordSnapshot("sample_document", Ids.New(), 1, "t", content);

        var otherRecord = signed with { RecordId = Ids.New() };
        var otherVersion = signed with { Version = 2 };
        var otherType = signed with { RecordType = "another_document" };

        Assert.NotEqual(SnapshotCanonicalForm.Hash(signed), SnapshotCanonicalForm.Hash(otherRecord));
        Assert.NotEqual(SnapshotCanonicalForm.Hash(signed), SnapshotCanonicalForm.Hash(otherVersion));
        Assert.NotEqual(SnapshotCanonicalForm.Hash(signed), SnapshotCanonicalForm.Hash(otherType));
        Assert.Equal(SnapshotCanonicalForm.Hash(signed), SnapshotCanonicalForm.Hash(signed with { Title = "Another title" }));
    }

    /// <summary>The README's rules, written again from the text: NFC text, few escapes, keys in ordinal order, plain numbers.</summary>
    private static string IndependentCanonical(RecordSnapshot snapshot) =>
        "[" + string.Join(",", Text("iron-snapshot-v1"), Text(snapshot.RecordType), Text(snapshot.RecordId.ToString("D")), snapshot.Version.ToString(CultureInfo.InvariantCulture), Json(snapshot.Content)) + "]";

    private static string Json(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => Text(p.Name) + ":" + Json(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Json)) + "]",
        JsonValueKind.String => Text(e.GetString()!),
        JsonValueKind.Number => Number(e.GetDecimal()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null",
    };

    private static string Number(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text is "-0" or "" ? "0" : text;
    }

    private static string Text(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value.Normalize(NormalizationForm.FormC))
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
