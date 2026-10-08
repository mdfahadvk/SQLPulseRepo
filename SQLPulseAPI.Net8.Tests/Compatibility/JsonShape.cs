using System.Text.Json;
using Xunit;

namespace SQLPulseAPI.Net8.Tests.Compatibility;

/// <summary>
/// Compares two JSON documents by shape rather than by value.
/// </summary>
/// <remarks>
/// A migration is compatible when a client cannot tell the implementations apart, and what
/// a client actually binds to is the set of property names and the type of each value.
/// Generated values — identifiers, timestamps, durations — legitimately differ between two
/// runs of the same implementation, so asserting on them would test nothing useful and
/// would fail for the wrong reasons.
/// </remarks>
internal static class JsonShape
{
    /// <summary>
    /// Asserts that <paramref name="actualJson"/> has the same property structure as
    /// <paramref name="expectedJson"/>.
    /// </summary>
    /// <param name="expectedJson">The Python golden master.</param>
    /// <param name="actualJson">The .NET response.</param>
    /// <param name="ignore">
    /// Property paths that may legitimately differ, given as dotted paths with <c>[]</c>
    /// for array elements, e.g. <c>candidates[].rank</c>.
    /// </param>
    public static void AssertMatches(string expectedJson, string actualJson, params string[] ignore)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);

        var ignored = new HashSet<string>(ignore, StringComparer.Ordinal);
        Compare(expected.RootElement, actual.RootElement, "$", ignored);
    }

    private static void Compare(
        JsonElement expected,
        JsonElement actual,
        string path,
        HashSet<string> ignored)
    {
        if (ignored.Contains(Normalize(path)))
        {
            return;
        }

        // A null in the golden master only tells us the property is nullable, not what it
        // holds when populated, so the value's type cannot be checked from it.
        if (expected.ValueKind == JsonValueKind.Null || actual.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(expected, actual, path, ignored);
                break;

            case JsonValueKind.Array:
                CompareArrays(expected, actual, path, ignored);
                break;

            default:
                AssertSameValueKind(expected, actual, path);
                break;
        }
    }

    private static void CompareObjects(
        JsonElement expected,
        JsonElement actual,
        string path,
        HashSet<string> ignored)
    {
        Assert.True(
            actual.ValueKind == JsonValueKind.Object,
            $"{path}: expected an object, got {actual.ValueKind}");

        var actualNames = actual.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // A free-form dictionary (metrics, arguments_sanitized, result_summary) carries
        // caller-defined keys, so its key set is data rather than contract.
        if (IsOpenMap(path))
        {
            return;
        }

        foreach (var property in expected.EnumerateObject())
        {
            var childPath = $"{path}.{property.Name}";
            if (ignored.Contains(Normalize(childPath)))
            {
                continue;
            }

            Assert.True(
                actualNames.Contains(property.Name),
                $"{childPath}: missing from the .NET response. "
                + $"Present: {string.Join(", ", actualNames.OrderBy(n => n, StringComparer.Ordinal))}");

            Compare(property.Value, actual.GetProperty(property.Name), childPath, ignored);
        }

        var extra = actualNames
            .Except(expectedNames, StringComparer.Ordinal)
            .Where(name => !ignored.Contains(Normalize($"{path}.{name}")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            extra.Count == 0,
            $"{path}: the .NET response adds properties Python does not emit: {string.Join(", ", extra)}");
    }

    private static void CompareArrays(
        JsonElement expected,
        JsonElement actual,
        string path,
        HashSet<string> ignored)
    {
        Assert.True(
            actual.ValueKind == JsonValueKind.Array,
            $"{path}: expected an array, got {actual.ValueKind}");

        // Element counts are data, not contract. The element shape is what a client binds
        // to, so it is compared only when both sides have something to compare.
        if (expected.GetArrayLength() == 0 || actual.GetArrayLength() == 0)
        {
            return;
        }

        Compare(expected[0], actual[0], $"{path}[]", ignored);
    }

    private static void AssertSameValueKind(JsonElement expected, JsonElement actual, string path)
    {
        // Python emits 0.0 for a float and 0 for an int; both are JSON numbers, and a
        // client reading either gets a JavaScript number.
        var expectedKind = Simplify(expected.ValueKind);
        var actualKind = Simplify(actual.ValueKind);

        Assert.True(
            expectedKind == actualKind,
            $"{path}: expected {expectedKind} (Python), got {actualKind} (.NET)");
    }

    private static JsonValueKind Simplify(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => JsonValueKind.True,
        _ => kind,
    };

    /// <summary>Collapses array indexes so an ignore path does not need to name one.</summary>
    private static string Normalize(string path) => path.Replace("$.", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Whether a path holds a caller-defined map rather than a fixed set of properties.
    /// </summary>
    private static bool IsOpenMap(string path) =>
        path.EndsWith(".metrics", StringComparison.Ordinal)
        || path.EndsWith(".arguments_sanitized", StringComparison.Ordinal)
        || path.EndsWith(".arguments", StringComparison.Ordinal)
        || path.EndsWith(".result_summary", StringComparison.Ordinal);
}
