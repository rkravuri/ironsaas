using System.Reflection;
using System.Text.Json;

namespace Iron.Api.Lists;

/// <summary>
/// Cuts response items down to the fields the caller asked for (<c>fields=id,name</c>). Field names are the snake_case
/// names of the item's properties, so a list's field names must match its response shape.
/// </summary>
public static class FieldShaper
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Shape<T>(IReadOnlyList<T> items, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fields);

        var propertiesByType = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
        var result = new List<IReadOnlyDictionary<string, object?>>(items.Count);
        foreach (var item in items)
        {
            var shaped = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (item is not null)
            {
                var properties = PropertiesOf(item.GetType(), propertiesByType);
                foreach (var field in fields)
                {
                    if (properties.TryGetValue(field, out var property))
                    {
                        shaped[field] = property.GetValue(item);
                    }
                }
            }

            result.Add(shaped);
        }

        return result;
    }

    private static Dictionary<string, PropertyInfo> PropertiesOf(Type type, Dictionary<Type, Dictionary<string, PropertyInfo>> cache)
    {
        if (!cache.TryGetValue(type, out var properties))
        {
            properties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    properties[JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)] = property;
                }
            }

            cache[type] = properties;
        }

        return properties;
    }
}
