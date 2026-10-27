using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Iron.Analyzers;

/// <summary>
/// SQL is never built from strings (TF-071 AC3, ADR-0053). The text handed to EF Core's raw SQL methods
/// (<c>FromSqlRaw</c>, <c>ExecuteSqlRaw</c>, <c>ExecuteSqlRawAsync</c>, <c>SqlQueryRaw</c>), to a command's constructor
/// (<c>NpgsqlCommand</c>, <c>NpgsqlBatchCommand</c>), to a data source's <c>CreateCommand</c> or to a command's
/// <c>CommandText</c> is a compile-time constant;
/// values go in as parameters, or through the interpolated forms (<c>FromSql</c>, <c>ExecuteSql</c>), which make them
/// parameters. A method that must name roles or schemas, which PostgreSQL takes no parameters for, says why with
/// <c>[SqlFromIdentifiers("...")]</c>, and a reviewer reads it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SqlFromStringsAnalyzer : DiagnosticAnalyzer
{
    public const string BuiltFromStringsId = "IRON0010";
    public const string ExemptionWithoutReasonId = "IRON0011";

    private const string Category = "Iron.Security";
    private const string ExemptionAttribute = "Iron.Kernel.SqlFromIdentifiersAttribute";

    private static readonly ImmutableHashSet<string> RawMethods = ImmutableHashSet.Create("FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw");

    public static readonly DiagnosticDescriptor BuiltFromStrings = new(
        BuiltFromStringsId,
        "SQL is not built from strings",
        "The SQL text of {0} is built at run time: pass constant text with parameters ({{0}}, @name, NpgsqlParameter) or use FromSql or ExecuteSql with an interpolated string, which makes its values parameters (ADR-0053)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExemptionWithoutReason = new(
        ExemptionWithoutReasonId,
        "An exemption says why",
        "[SqlFromIdentifiers] on '{0}' gives no reason: say which identifiers the SQL names and how they are checked (ADR-0053)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(BuiltFromStrings, ExemptionWithoutReason);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Invocation, OperationKind.Invocation);
        context.RegisterOperationAction(Creation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(Assignment, OperationKind.SimpleAssignment);
        context.RegisterSymbolAction(Exemption, SymbolKind.Method, SymbolKind.NamedType);
    }

    private static void Invocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name == "CreateCommand" && Derives(method.ContainingType, "System.Data.Common.DbDataSource"))
        {
            // A data source's command made with its text: NpgsqlDataSource.CreateCommand("...").
            var text = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "commandText");
            Check(context, text?.Value, $"{method.ContainingType.Name}.CreateCommand");
            return;
        }

        if (!RawMethods.Contains(method.Name) || !InNamespace(method.ContainingType, "Microsoft.EntityFrameworkCore"))
        {
            return;
        }

        var sql = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "sql");
        Check(context, sql?.Value, method.Name);
    }

    private static void Creation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol type || !IsCommand(type))
        {
            return;
        }

        var text = creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "cmdText" || argument.Parameter?.Name == "commandText");
        Check(context, text?.Value, $"new {type.Name}");
    }

    private static void Assignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is IPropertyReferenceOperation { Property.Name: "CommandText" } property && IsCommand(property.Property.ContainingType))
        {
            Check(context, assignment.Value, $"{property.Property.ContainingType.Name}.CommandText");
        }
    }

    private static void Check(OperationAnalysisContext context, IOperation? text, string what)
    {
        if (text is null)
        {
            return;
        }

        var value = text;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        if (value.ConstantValue.HasValue || Exempt(context.ContainingSymbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(BuiltFromStrings, text.Syntax.GetLocation(), what));
    }

    private static void Exemption(SymbolAnalysisContext context)
    {
        foreach (var attribute in context.Symbol.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == ExemptionAttribute))
        {
            var reason = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(reason))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? context.Symbol.Locations.FirstOrDefault();
                context.ReportDiagnostic(Diagnostic.Create(ExemptionWithoutReason, location, context.Symbol.Name));
            }
        }
    }

    /// <summary>The method, or a lambda's or local function's enclosing method or type, carries the exemption with a reason.</summary>
    private static bool Exempt(ISymbol? symbol)
    {
        for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingSymbol)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == ExemptionAttribute
                    && attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is string reason
                    && !string.IsNullOrWhiteSpace(reason))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A database command: a <c>DbCommand</c> or <c>DbBatchCommand</c>, or anything implementing <c>IDbCommand</c>.</summary>
    private static bool IsCommand(INamedTypeSymbol type) =>
        Derives(type, "System.Data.Common.DbCommand") || Derives(type, "System.Data.Common.DbBatchCommand")
        || type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Data.IDbCommand");

    private static bool Derives(INamedTypeSymbol? type, string baseName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InNamespace(INamedTypeSymbol? type, string name)
    {
        var space = type?.ContainingNamespace?.ToDisplayString();
        return space is not null && (space == name || space.StartsWith(name + ".", System.StringComparison.Ordinal));
    }
}
