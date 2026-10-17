using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Api.Json;

/// <summary>The one JSON shape of the API (TF-100): snake_case names, enums as snake_case strings, invariant everywhere.</summary>
public static class IronJson
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>Options for code that serialises outside the ASP.NET Core pipeline (tests, jobs).</summary>
    public static JsonSerializerOptions Default { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
