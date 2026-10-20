using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Iron.Testing.Analyzers;

/// <summary>
/// Every test method names what it proves (ADR-0044, TF-218): a test method, one with xUnit's <c>[Fact]</c>,
/// <c>[Theory]</c> or another fact attribute, carries <c>[Proves(...)]</c> with requirement criteria, requirements or
/// decisions in their form, or <c>[Untraced("why")]</c> alone. Whether an identifier exists is <c>make trace</c>'s
/// question: it reads the requirement documents and the ADRs, which the compiler does not see.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProvesAnalyzer : DiagnosticAnalyzer
{
    public const string MissingId = "IRON0005";
    public const string MalformedId = "IRON0006";
    public const string UntracedBesideProvesId = "IRON0007";

    private const string Category = "Iron.Testing";
    private const string ProvesAttribute = "Iron.Testing.ProvesAttribute";
    private const string UntracedAttribute = "Iron.Testing.UntracedAttribute";

    // The one grammar, as tools/trace.py and the shell's lint rule read it: a criterion "TF-033 AC2", a requirement
    // "TF-033" (any document's prefix of two or three letters), or a decision "ADR-0029".
    private static readonly Regex Identifier = new(@"^(?:[A-Z]{2,3}-[0-9]{3}(?: AC[1-9][0-9]*)?|ADR-[0-9]{4})$", RegexOptions.CultureInvariant);

    public static readonly DiagnosticDescriptor Missing = new(
        MissingId,
        "A test names what it proves",
        "Test method '{0}' does not name what it proves: add [Proves(\"TF-033 AC2\")] with the requirement criteria it proves, [Proves(\"ADR-0029\")] for a decision, or [Untraced(\"why\")] when it proves neither (ADR-0044)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Malformed = new(
        MalformedId,
        "An identifier in the form of a criterion, a requirement or a decision",
        "'{0}' is not a requirement criterion (TF-033 AC2), a requirement (TF-033) or a decision (ADR-0029) (ADR-0044)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UntracedBesideProves = new(
        UntracedBesideProvesId,
        "Untraced stands alone",
        "Test method '{0}' is both [Untraced] and [Proves]: a test that proves something names it, and only one that proves nothing a document states is untraced (ADR-0044)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Missing, Malformed, UntracedBesideProves);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        var attributes = method.GetAttributes();
        if (!attributes.Any(a => IsFact(a.AttributeClass)))
        {
            return;
        }

        var proves = attributes.Where(a => a.AttributeClass?.ToDisplayString() == ProvesAttribute).ToList();
        var untraced = attributes.Any(a => a.AttributeClass?.ToDisplayString() == UntracedAttribute);
        if (proves.Count == 0 && !untraced)
        {
            context.ReportDiagnostic(Diagnostic.Create(Missing, method.Locations.FirstOrDefault(), method.Name));
            return;
        }

        if (proves.Count > 0 && untraced)
        {
            context.ReportDiagnostic(Diagnostic.Create(UntracedBesideProves, method.Locations.FirstOrDefault(), method.Name));
        }

        foreach (var attribute in proves)
        {
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? method.Locations.FirstOrDefault();
            var identifiers = attribute.ConstructorArguments.SelectMany(Values).ToList();
            if (identifiers.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Malformed, location, string.Empty));
            }

            foreach (var identifier in identifiers)
            {
                if (identifier is null || !Identifier.IsMatch(identifier))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Malformed, location, identifier ?? "null"));
                }
            }
        }
    }

    /// <summary>The strings of a constructor argument: <c>params string[]</c> arrives as an array.</summary>
    private static IEnumerable<string?> Values(TypedConstant argument) =>
        argument.Kind == TypedConstantKind.Array
            ? argument.Values.Select(value => value.Value as string)
            : new[] { argument.Value as string };

    /// <summary>xUnit's FactAttribute or one derived from it (Theory and the others); in v3 also anything that is an IFactAttribute.</summary>
    private static bool IsFact(INamedTypeSymbol? attribute)
    {
        for (var type = attribute; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "Xunit.FactAttribute")
            {
                return true;
            }
        }

        return attribute?.AllInterfaces.Any(i => i.ToDisplayString() == "Xunit.v3.IFactAttribute") == true;
    }
}
