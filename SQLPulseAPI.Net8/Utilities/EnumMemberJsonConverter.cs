using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Utilities;

/// <summary>
/// Serializes enums using the literal string declared on
/// <see cref="EnumMemberAttribute"/>, so the JSON contract matches the Python
/// reference implementation exactly (for example <c>critical</c>, <c>HEALTHY</c>,
/// <c>SLOW_QUERY_CANDIDATE</c>). Falls back to the CLR member name when no
/// attribute is present.
/// </summary>
public sealed class EnumMemberJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToWire = BuildToWire();
    private static readonly Dictionary<string, TEnum> FromWire = BuildFromWire();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string value for enum {typeof(TEnum).Name}.");
        }

        var raw = reader.GetString();
        if (raw is not null && FromWire.TryGetValue(raw, out var value))
        {
            return value;
        }

        throw new JsonException($"'{raw}' is not a valid value for enum {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ToWire.TryGetValue(value, out var wire) ? wire : value.ToString());
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(ToWire.TryGetValue(value, out var wire) ? wire : value.ToString());
    }

    private static Dictionary<TEnum, string> BuildToWire()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            map[value] = field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? field.Name;
        }

        return map;
    }

    private static Dictionary<string, TEnum> BuildFromWire()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var pair in ToWire)
        {
            map[pair.Value] = pair.Key;
        }

        return map;
    }

    internal static string ToWireValue(TEnum value) => ToWire[value];

    /// <summary>
    /// Parses the literal wire value (for example <c>APPLICATION_OR_INTERACTIVE</c>) used
    /// by query-string parameters, so URLs and JSON accept the same spelling.
    /// </summary>
    internal static bool TryFromWireValue(string? raw, out TEnum value)
    {
        if (raw is not null && FromWire.TryGetValue(raw, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Every accepted wire value, in declaration order.</summary>
    internal static IReadOnlyList<string> WireValues { get; } = ToWire.Values.ToArray();
}

/// <summary>
/// Applies <see cref="EnumMemberJsonConverter{TEnum}"/> to every enum encountered
/// during serialization.
/// </summary>
public sealed class EnumMemberJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(EnumMemberJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}
