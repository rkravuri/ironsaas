using Iron.Kernel.History;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// The names of the audit event types a host's modules declare (ADR-0051), registered by AddModules. An event whose type no
/// module declares, such as one written by a module this host does not run, reads as its code.
/// </summary>
public sealed class AuditEventNames
{
    private readonly Dictionary<string, AuditEventType> _types = new(StringComparer.Ordinal);

    public AuditEventNames(IEnumerable<AuditEventType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            if (_types.TryGetValue(type.Code, out var known) && !string.Equals(known.Name, type.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The audit event type '{type.Code}' is declared twice with different names.");
            }

            _types[type.Code] = type;
        }
    }

    /// <summary>Every declared type, by code.</summary>
    public IReadOnlyList<AuditEventType> All => [.. _types.Values.OrderBy(t => t.Code, StringComparer.Ordinal)];

    /// <summary>The codes of the types declared as an administrator's actions (TF-067, ADR-0052), which an evidence pack lists.</summary>
    public IReadOnlyList<string> Administrative => [.. _types.Values.Where(t => t.Administrative).Select(t => t.Code).Order(StringComparer.Ordinal)];

    /// <summary>The words an event type reads as, or its code when no module declares it.</summary>
    public string NameOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _types.TryGetValue(code, out var type) ? type.Name : code;
    }
}
