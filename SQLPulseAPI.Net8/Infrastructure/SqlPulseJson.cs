using System.Text.Json;
using System.Text.Json.Serialization;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Infrastructure;

/// <summary>
/// Single source of truth for JSON serialization so the .NET API emits the same
/// payload shape as the Python/FastAPI reference implementation:
/// snake_case property names (declared explicitly via <c>JsonPropertyName</c>),
/// string enums using their Python literal values, and no property renaming.
/// </summary>
public static class SqlPulseJson
{
    /// <summary>Shared options instance used by the API and by contract tests.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Creates a fresh options instance configured for the SQLPulse contract.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }

    /// <summary>Applies the SQLPulse contract settings to an existing options instance.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        // Property names are declared explicitly with [JsonPropertyName]; disabling
        // the naming policy guarantees nothing is silently camel-cased.
        options.PropertyNamingPolicy = null;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;

        if (!options.Converters.Any(c => c is EnumMemberJsonConverterFactory))
        {
            options.Converters.Add(new EnumMemberJsonConverterFactory());
        }
    }
}
