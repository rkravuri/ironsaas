using Iron.Core.Permissions;

namespace Iron.Api.Permissions;

/// <summary>Every permission the host's modules declared (TF-030). Built by AddModules; the role editor and the seeder read it.</summary>
public sealed class PermissionCatalogue
{
    private readonly Dictionary<string, Permission> _byName = new(StringComparer.Ordinal);

    public PermissionCatalogue(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var ordered = new List<Permission>();
        foreach (var permission in permissions)
        {
            if (!_byName.TryAdd(permission.Name, permission))
            {
                throw new InvalidOperationException($"The permission '{permission.Name}' is declared twice.");
            }

            ordered.Add(permission);
        }

        All = ordered;
    }

    public IReadOnlyList<Permission> All { get; }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public Permission? Find(string name) => _byName.GetValueOrDefault(name);
}
