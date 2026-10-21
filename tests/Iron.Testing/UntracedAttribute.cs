using Xunit.v3;

namespace Iron.Testing;

/// <summary>
/// A test that proves nothing a requirement or an ADR states, with the reason (ADR-0044). It is not tagged to fit:
/// <c>make trace</c> lists it under "Tests that prove no requirement and no decision" for the design session, which
/// writes the requirement or the decision it proves, or removes the test. It stands alone: the analyzer refuses it
/// beside <see cref="ProvesAttribute"/> (IRON0007). Its trait is <c>proves=untraced</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UntracedAttribute : Attribute, ITraitAttribute
{
    public const string TraitValue = "untraced";

    public UntracedAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(ProvesAttribute.TraitName, TraitValue)];
}
