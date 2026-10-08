using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// Strips credentials and trims oversized payloads before anything reaches the LLM
/// provider. Ported from <c>app/agent/sanitizer.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every byte handed to a third-party model leaves SQLPulse's trust boundary, so tool
/// output passes through here first. Two separate concerns are handled: removing secrets,
/// which is a security requirement, and shrinking payloads, which keeps a single large
/// snapshot from exhausting the provider's token budget.
/// </para>
/// <para>
/// The SQL password, the full connection string and the LLM API key are never placed in a
/// tool result to begin with; this is the second line of defence.
/// </para>
/// </remarks>
public static partial class LlmSanitizer
{
    /// <summary>Replacement written in place of any secret.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>Marks a payload that was shortened to fit the token budget.</summary>
    public const string TruncatedFlag = "_truncated";

    private const string TruncationNoteKey = "_truncation_note";

    private const string TruncationNote =
        "Results were truncated to fit the model's token budget. "
        + "Counts in this payload reflect the full result set; the item "
        + "lists show only the highest-priority entries.";

    /// <summary>Closes the JSON object so a hard-cut payload still parses, and flags the cut.</summary>
    private const string HardCutSuffix = "...\"_truncated\":true}";

    private const string TruncationSuffix = "\u2026[truncated]";

    private const int VerboseTextLimit = 300;

    private const int DefaultSanitizeDepth = 6;

    private const int DefaultTrimDepth = 8;

    /// <summary>Number of shrink passes before falling back to a hard cut.</summary>
    private const int MaxShrinkPasses = 8;

    /// <summary>
    /// Property names whose value is replaced outright, whatever it contains.
    /// </summary>
    /// <remarks>
    /// Matching is on the name rather than the value because a secret is not always
    /// recognisable: an API key is just a string of characters.
    /// </remarks>
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pwd", "secret", "api_key", "apikey",
        "connection_string", "conn_str", "sql_password",
        "token", "access_token", "refresh_token",
    };

    /// <summary>
    /// Fields that routinely carry multi-kilobyte SQL or plan text. Only a preview is ever
    /// useful to the model, and full text quickly exhausts the token budget.
    /// </summary>
    private static readonly HashSet<string> VerboseTextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql_text", "sql_text_preview", "query_text", "statement_text",
        "query_sql_text", "command_text", "batch_text", "query_plan",
        "plan_xml", "execution_plan",
    };

    private static readonly Regex[] SensitivePatterns =
    [
        PasswordAssignment(),
        ApiKeyAssignment(),
        BearerToken(),
        ConnectionStringAssignment(),
        UidPwdPair(),
        ServerPwdPair(),
    ];

    /// <summary>Removes sensitive values from a text string. Python: <c>sanitize_text</c>.</summary>
    public static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var pattern in SensitivePatterns)
        {
            result = pattern.Replace(result, Redacted);
        }

        return result;
    }

    /// <summary>
    /// Recursively redacts sensitive keys and values. Python: <c>sanitize_dict</c>.
    /// </summary>
    /// <param name="node">Node to sanitize. Never mutated; a new tree is returned.</param>
    /// <param name="maxDepth">Recursion limit, guarding against pathological nesting.</param>
    /// <remarks>
    /// Unlike the Python reference, strings nested inside arrays are sanitized too. The
    /// reference copies them through untouched, which would let a credential inside a list
    /// of strings reach the provider.
    /// </remarks>
    public static JsonNode? Sanitize(JsonNode? node, int maxDepth = DefaultSanitizeDepth)
    {
        if (maxDepth <= 0)
        {
            return new JsonObject { [TruncatedFlag] = true };
        }

        switch (node)
        {
            case JsonObject source:
            {
                var sanitized = new JsonObject();
                foreach (var (key, value) in source)
                {
                    sanitized[key] = SensitiveKeys.Contains(NormalizeKey(key))
                        ? Redacted
                        : Sanitize(value, maxDepth - 1);
                }

                return sanitized;
            }

            case JsonArray source:
            {
                var sanitized = new JsonArray();
                foreach (var item in source)
                {
                    sanitized.Add(Sanitize(item, maxDepth - 1));
                }

                return sanitized;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return SanitizeText(text);

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Prepares tool output for the model: strips credentials, then trims verbose fields.
    /// Python: <c>sanitize_for_llm</c>.
    /// </summary>
    public static JsonObject SanitizeForLlm(JsonObject payload)
    {
        var sanitized = Sanitize(payload) as JsonObject ?? [];
        return TrimVerboseFields(sanitized) as JsonObject ?? [];
    }

    /// <summary>
    /// Recursively shortens known-verbose text fields anywhere in the payload.
    /// Python: <c>_trim_verbose_fields</c>.
    /// </summary>
    public static JsonNode? TrimVerboseFields(JsonNode? node, int maxDepth = DefaultTrimDepth)
    {
        if (maxDepth <= 0)
        {
            return node?.DeepClone();
        }

        switch (node)
        {
            case JsonObject source:
            {
                var trimmed = new JsonObject();
                foreach (var (key, value) in source)
                {
                    if (VerboseTextKeys.Contains(key)
                        && value is JsonValue candidate
                        && candidate.TryGetValue<string>(out var text)
                        && text.Length > VerboseTextLimit)
                    {
                        var keep = VerboseTextLimit - TruncationSuffix.Length;
                        trimmed[key] = string.Concat(text.AsSpan(0, keep), TruncationSuffix);
                    }
                    else
                    {
                        trimmed[key] = TrimVerboseFields(value, maxDepth - 1);
                    }
                }

                return trimmed;
            }

            case JsonArray source:
            {
                var trimmed = new JsonArray();
                foreach (var item in source)
                {
                    trimmed.Add(TrimVerboseFields(item, maxDepth - 1));
                }

                return trimmed;
            }

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Serialises tool output to JSON, shrinking it to fit <paramref name="maxChars"/>.
    /// Python: <c>serialize_for_llm</c>.
    /// </summary>
    /// <remarks>
    /// An oversized payload is reduced by repeatedly halving its longest array — usually
    /// the culprit — before falling back to a hard cut, so the model still receives
    /// well-formed, representative evidence rather than a truncated fragment. Counts stay
    /// intact, and a note explains that the lists were shortened, so the model does not
    /// report "3 blocking sessions" when there were thirty.
    /// </remarks>
    public static string SerializeForLlm(JsonObject payload, int maxChars)
    {
        var json = payload.ToJsonString();
        if (json.Length <= maxChars)
        {
            return json;
        }

        var shrunk = payload.DeepClone().AsObject();

        for (var pass = 0; pass < MaxShrinkPasses; pass++)
        {
            var longest = LongestArrayKey(shrunk);
            if (longest is null)
            {
                break;
            }

            var array = shrunk[longest]!.AsArray();
            var kept = Math.Max(1, array.Count / 2);
            var reduced = new JsonArray();
            for (var index = 0; index < kept; index++)
            {
                reduced.Add(array[index]?.DeepClone());
            }

            shrunk[longest] = reduced;
            shrunk[TruncatedFlag] = true;
            shrunk[TruncationNoteKey] = TruncationNote;

            json = shrunk.ToJsonString();
            if (json.Length <= maxChars)
            {
                return json;
            }
        }

        var budget = Math.Max(0, maxChars - HardCutSuffix.Length);
        return string.Concat(json.AsSpan(0, Math.Min(budget, json.Length)), HardCutSuffix);
    }

    /// <summary>Name of the longest array with more than one element, or null when none remain.</summary>
    private static string? LongestArrayKey(JsonObject payload)
    {
        string? longest = null;
        var longestCount = 1;

        foreach (var (key, value) in payload)
        {
            if (value is JsonArray array && array.Count > longestCount)
            {
                longest = key;
                longestCount = array.Count;
            }
        }

        return longest;
    }

    /// <summary>Normalises a property name so <c>api-key</c> and <c>api_key</c> match alike.</summary>
    private static string NormalizeKey(string key) => key.Replace('-', '_');

    [GeneratedRegex(@"(password|pwd)\s*=\s*[^\s;]+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordAssignment();

    [GeneratedRegex(@"(api[_-]?key|secret[_-]?key)\s*=\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyAssignment();

    [GeneratedRegex(@"bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(connection\s*string|conn\s*str)\s*=\s*.+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringAssignment();

    [GeneratedRegex(@"UID=\S+;\s*PWD=\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UidPwdPair();

    [GeneratedRegex(@"SERVER=\S+;.*PWD=[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex ServerPwdPair();
}
