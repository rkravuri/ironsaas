using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Iron.Architecture.Tests;

/// <summary>A member of the .NET library that Foundation code must not call, and what to use instead.</summary>
public sealed record ForbiddenMember(string Namespace, string TypeName, string MemberName, string UseInstead)
{
    public string DisplayName => $"{Namespace}.{TypeName}.{MemberName.Replace("get_", string.Empty, StringComparison.Ordinal)}";
}

/// <summary>
/// Reads the member references of a compiled assembly and reports calls to forbidden members.
/// First rule (ADR-0007): time comes from an injected TimeProvider, never from the system clock directly,
/// so that every test can control it.
/// </summary>
public static class ForbiddenMemberRules
{
    public static readonly IReadOnlyList<ForbiddenMember> Members =
    [
        new("System", "DateTime", "get_Now", "Inject TimeProvider and call GetLocalNow()."),
        new("System", "DateTime", "get_UtcNow", "Inject TimeProvider and call GetUtcNow()."),
        new("System", "DateTime", "get_Today", "Inject TimeProvider and take the date of GetLocalNow()."),
        new("System", "DateTimeOffset", "get_Now", "Inject TimeProvider and call GetLocalNow()."),
        new("System", "DateTimeOffset", "get_UtcNow", "Inject TimeProvider and call GetUtcNow()."),
    ];

    public static IReadOnlyList<string> Check(string assemblyPath)
    {
        return FindForbidden(Path.GetFileName(assemblyPath), ReadReferencedMembers(assemblyPath));
    }

    /// <summary>Pure rule over a list of referenced members.</summary>
    public static IReadOnlyList<string> FindForbidden(
        string assemblyLabel,
        IEnumerable<(string Namespace, string TypeName, string MemberName)> referencedMembers)
    {
        var violations = new List<string>();
        foreach (var (ns, typeName, memberName) in referencedMembers)
        {
            foreach (var forbidden in Members)
            {
                if (ns == forbidden.Namespace && typeName == forbidden.TypeName && memberName == forbidden.MemberName)
                {
                    violations.Add($"{assemblyLabel}: uses {forbidden.DisplayName}. {forbidden.UseInstead}");
                }
            }
        }

        return violations;
    }

    /// <summary>Every member of another assembly that this assembly refers to, as (namespace, type, member).</summary>
    public static IReadOnlyList<(string Namespace, string TypeName, string MemberName)> ReadReferencedMembers(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        var metadata = peReader.GetMetadataReader();
        var result = new List<(string Namespace, string TypeName, string MemberName)>();
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            result.Add((metadata.GetString(type.Namespace), metadata.GetString(type.Name), metadata.GetString(member.Name)));
        }

        return result;
    }
}
