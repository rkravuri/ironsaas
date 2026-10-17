using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Iron.Architecture.Tests;

/// <summary>
/// Reads the type references of a compiled assembly and reports any that point into another module's
/// private namespaces. A module's Api, Services and Data namespaces are private; its Contracts namespace
/// and its root namespace (the module class used by hosts) are public.
/// </summary>
public static class AssemblyReferenceRules
{
    /// <summary>The namespace parts, under a module's root namespace, that no other assembly may use.</summary>
    public static readonly string[] PrivateFolders = ["Api", "Services", "Data"];

    public static IReadOnlyList<string> Check(string assemblyPath, string? ownModule, IEnumerable<string> allModules)
    {
        return FindForbidden(Path.GetFileName(assemblyPath), ReadReferencedTypes(assemblyPath), ownModule, allModules);
    }

    /// <summary>Pure rule: which of the given type references point into another module's private namespaces.</summary>
    public static IReadOnlyList<string> FindForbidden(
        string assemblyLabel,
        IEnumerable<(string Namespace, string Name)> referencedTypes,
        string? ownModule,
        IEnumerable<string> allModules)
    {
        var forbiddenPrefixes = new List<string>();
        foreach (var module in allModules)
        {
            if (string.Equals(module, ownModule, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var folder in PrivateFolders)
            {
                forbiddenPrefixes.Add(module + "." + folder);
            }
        }

        var violations = new List<string>();
        foreach (var (ns, typeName) in referencedTypes)
        {
            foreach (var prefix in forbiddenPrefixes)
            {
                if (ns.Equals(prefix, StringComparison.Ordinal) || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    violations.Add($"{assemblyLabel}: uses {ns}.{typeName}. The namespace {prefix} is private to its module; only its Contracts are public.");
                    break;
                }
            }
        }

        return violations;
    }

    /// <summary>Every type the assembly refers to in other assemblies, as (namespace, name).</summary>
    public static IReadOnlyList<(string Namespace, string Name)> ReadReferencedTypes(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        var metadata = peReader.GetMetadataReader();
        var result = new List<(string Namespace, string Name)>();
        foreach (var handle in metadata.TypeReferences)
        {
            var typeReference = metadata.GetTypeReference(handle);
            result.Add((NamespaceOf(metadata, typeReference), metadata.GetString(typeReference.Name)));
        }

        return result;
    }

    // A nested type has no namespace of its own; it belongs to its declaring type. Walk up to the outer type.
    private static string NamespaceOf(MetadataReader metadata, TypeReference typeReference)
    {
        var current = typeReference;
        while (current.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            current = metadata.GetTypeReference((TypeReferenceHandle)current.ResolutionScope);
        }

        return metadata.GetString(current.Namespace);
    }
}
