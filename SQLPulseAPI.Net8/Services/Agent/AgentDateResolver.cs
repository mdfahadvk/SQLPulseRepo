using System.Globalization;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// Turns the date expressions a model produces into concrete timestamps.
/// Ported from <c>_parse_datetime</c> in <c>app/agent/tools.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Date handling is resolved here, deterministically, rather than being left to the model.
/// Language models routinely emit a year from their training data — asking about "the 5th
/// of January" can yield <c>2024-01-05</c> while the server is running in 2026 — and a
/// silently wrong year turns into an empty result set that reads like a healthy server.
/// </para>
/// <para>
/// Resolved values are returned as UTC, matching the convention used by the collectors and
/// history services that consume them.
/// </para>
/// </remarks>
public static class AgentDateResolver
{
    /// <summary>
    /// Accepted ISO 8601 layouts.
    /// </summary>
    /// <remarks>
    /// Exact formats are used instead of a lenient parse so an ambiguous value such as
    /// <c>05/01/2026</c> is rejected outright rather than being silently read as either the
    /// 5th of January or the 1st of May. The <c>K</c> specifier matches an absent offset,
    /// a <c>Z</c>, or an explicit <c>+hh:mm</c>.
    /// </remarks>
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mmK",
        "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Resolves a model-supplied date expression.</summary>
    /// <exception cref="FormatException">The value is not a supported date expression.</exception>
    public static DateTimeOffset Resolve(string? value) => Resolve(value, DateTimeOffset.UtcNow);

    /// <summary>Resolves a model-supplied date expression against an explicit "now".</summary>
    /// <exception cref="FormatException">The value is not a supported date expression.</exception>
    internal static DateTimeOffset Resolve(string? value, DateTimeOffset now) =>
        TryResolve(value, now, out var resolved)
            ? resolved
            : throw new FormatException($"Cannot parse datetime: '{value}'");

    /// <summary>Attempts to resolve a model-supplied date expression.</summary>
    public static bool TryResolve(string? value, out DateTimeOffset resolved) =>
        TryResolve(value, DateTimeOffset.UtcNow, out resolved);

    /// <summary>Attempts to resolve a model-supplied date expression against an explicit "now".</summary>
    internal static bool TryResolve(string? value, DateTimeOffset now, out DateTimeOffset resolved)
    {
        resolved = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var reference = now.UtcDateTime;

        switch (text.ToLowerInvariant())
        {
            case "today":
                resolved = AsUtc(reference.Date);
                return true;

            case "yesterday":
                resolved = AsUtc(reference.Date.AddDays(-1));
                return true;
        }

        // Python normalises the military "Z" suffix before parsing; "K" accepts it either way.
        if (text.EndsWith('Z'))
        {
            text = string.Concat(text.AsSpan(0, text.Length - 1), "+00:00");
        }

        if (!DateTimeOffset.TryParseExact(
                text,
                AcceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        // The wall-clock reading is kept and the offset discarded, matching the Python
        // reference and the direct REST endpoints, which treat an unqualified timestamp as
        // already being in the server's reporting timezone rather than shifting it.
        var wallClock = parsed.DateTime;

        resolved = AsUtc(NormalizeEndOfDay(CorrectYear(wallClock, reference)));
        return true;
    }

    /// <summary>
    /// Replaces an implausibly old year with the most recent past occurrence of the same
    /// month and day. Python: <c>_correct_year</c>.
    /// </summary>
    /// <remarks>
    /// The current year and the previous one are trusted as-is, because a user genuinely
    /// can ask about last December. Anything older is treated as a hallucinated year:
    /// a model has no reliable sense of today's date, so an unprompted 2024 in a 2026
    /// conversation is far more likely to be training-data bleed than a deliberate request
    /// for two-year-old data — which Query Store would not retain anyway.
    /// </remarks>
    internal static DateTime CorrectYear(DateTime value, DateTime now)
    {
        if (now.Year - value.Year <= 1)
        {
            return value;
        }

        var candidateThisYear = WithYear(value, now.Year);

        return candidateThisYear <= now
            ? candidateThisYear
            : WithYear(value, now.Year - 1);
    }

    /// <summary>
    /// Converts an inclusive <c>23:59:59</c> end time to the following midnight.
    /// Python: <c>_normalize_end_of_day</c>.
    /// </summary>
    /// <remarks>
    /// Models express "the whole of the 5th" as ending at 23:59:59, which against a
    /// half-open range would drop the final second of activity.
    /// </remarks>
    internal static DateTime NormalizeEndOfDay(DateTime value) =>
        value is { Hour: 23, Minute: 59, Second: >= 59 }
            ? value.Date.AddDays(1)
            : value;

    /// <summary>Rebuilds a date in another year, clamping 29 February onto a non-leap year.</summary>
    private static DateTime WithYear(DateTime value, int year)
    {
        var day = Math.Min(value.Day, DateTime.DaysInMonth(year, value.Month));
        return new DateTime(year, value.Month, day, 0, 0, 0, DateTimeKind.Unspecified)
            .Add(value.TimeOfDay);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero);
}
