using Xunit.v3;

namespace Iron.Testing;

/// <summary>
/// What a test proves (ADR-0044, TF-218): a requirement's criterion (<c>TF-033 AC2</c>, <c>REQ-012 AC1</c>), a whole
/// requirement when the test proves every criterion (<c>TF-033</c>), or a decision when the test proves a rule an ADR
/// decided rather than a criterion (<c>ADR-0029</c>). Several identifiers in one attribute, or the attribute several
/// times. <c>make trace</c> reads it from the source and refuses an identifier no document holds; the analyzer in
/// <c>Iron.Testing.Analyzers</c> refuses a test method without it (IRON0005) or with an identifier out of form (IRON0006).
/// </summary>
/// <remarks>
/// Each identifier is also an xUnit trait named <c>proves</c>, with the requirement alone beside a criterion, so a run
/// can choose by either: <c>dotnet test --filter "proves=TF-033"</c> runs every test of TF-033, and
/// <c>--filter "proves=TF-033 AC2"</c> those of that criterion.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ProvesAttribute : Attribute, ITraitAttribute
{
    public const string TraitName = "proves";

    public ProvesAttribute(params string[] identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        Identifiers = identifiers;
    }

    public IReadOnlyList<string> Identifiers { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        var traits = new List<KeyValuePair<string, string>>();
        foreach (var identifier in Identifiers)
        {
            traits.Add(new(TraitName, identifier));

            // "TF-033 AC2" also answers to "TF-033".
            var space = identifier.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                traits.Add(new(TraitName, identifier[..space]));
            }
        }

        return traits;
    }
}
