using System.Text.RegularExpressions;
using SQLPulseAPI.Net8.Models.Agent;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// Reads the structured investigation fields back out of the model's written answer.
/// Ported from <c>_parse_structured_answer</c> in <c>app/agent/engine.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// The prose answer is the single source of truth. Severity, confidence, evidence and the
/// rest are extracted from it rather than requested separately, so the narrative and the
/// structured fields cannot contradict each other — a client cannot be shown "severity:
/// high" beside an answer that says nothing is wrong.
/// </para>
/// <para>
/// Two shapes are understood. The flat "Label: value" form is the reference agent's, kept
/// so older answers and audit records still parse. The adaptive Markdown report is what
/// the agent writes now, where a healthy result is a few lines rather than a template
/// padded with "N/A". The flat pass runs first and the Markdown pass only fills what it
/// left unset, so neither can quietly override the other.
/// </para>
/// <para>
/// Parsing is best-effort by design. A model does not always follow a template exactly,
/// and a missing field is better than a failed investigation: the caller keeps the full
/// answer text either way, and unparsed fields fall back to their conservative defaults
/// (<c>unknown</c> severity, <c>low</c> confidence, human review required).
/// </para>
/// </remarks>
public static partial class InvestigationAnswerParser
{
    private static readonly HashSet<string> Severities =
        new(StringComparer.Ordinal) { "critical", "high", "medium", "low", "unknown" };

    private static readonly HashSet<string> Confidences =
        new(StringComparer.Ordinal) { "high", "medium", "low" };

    /// <summary>Values that mean "no value", written out rather than left blank.</summary>
    private static readonly HashSet<string> Absent =
        new(StringComparer.OrdinalIgnoreCase) { "n/a", "none" };

    /// <summary>
    /// How a report's headline status maps onto the severity a client is given.
    /// </summary>
    /// <remarks>
    /// The adaptive report states an outcome rather than a severity word, because
    /// "Status: HEALTHY" reads better than "Severity: low" at the top of a clean result.
    /// The distinction that matters is between a database that is fine — a real, low
    /// finding — and one that could not be assessed, which stays <c>unknown</c> so no
    /// client mistakes missing telemetry for a clean bill of health.
    /// </remarks>
    private static readonly Dictionary<string, string> StatusSeverities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = "critical",
            ["high"] = "high",
            ["medium"] = "medium",
            ["low"] = "low",
            ["healthy"] = "low",
            ["degraded"] = "medium",
            ["data unavailable"] = "unknown",
            ["insufficient data"] = "unknown",
            ["partial analysis"] = "unknown",
            ["unknown"] = "unknown",
        };

    /// <summary>Report subjects that name the instance rather than a database.</summary>
    private static readonly HashSet<string> ServerSubjects =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "server",
            "sql server",
            "sql server instance",
            "server-wide",
            "instance",
        };

    /// <summary>
    /// Outcomes that mean the question could not be answered from the available
    /// telemetry, and so cannot carry a determinate severity.
    /// </summary>
    private static readonly HashSet<string> UnassessableOutcomes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Historical Analysis Unavailable",
            "Insufficient Historical Data",
        };

    /// <summary>
    /// Applies every field the answer text declares onto <paramref name="response"/>.
    /// </summary>
    /// <remarks>
    /// Fields the model omitted are left at their existing values, so the response defaults
    /// stay conservative rather than being overwritten with blanks.
    /// </remarks>
    public static void Apply(InvestigationResponse response, string answer)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrEmpty(answer))
        {
            return;
        }

        foreach (var line in answer.Split('\n'))
        {
            // Markdown emphasis is stripped first so "**Severity:** high" is read the same
            // as "Severity: high"; models format the template both ways.
            var cleaned = Emphasis().Replace(line, string.Empty).Trim();
            var lower = cleaned.ToLowerInvariant();

            if (lower.StartsWith("severity:", StringComparison.Ordinal))
            {
                var value = ValueAfterColon(cleaned).ToLowerInvariant();
                if (Severities.Contains(value))
                {
                    response.Severity = value;
                }
            }
            else if (lower.StartsWith("confidence:", StringComparison.Ordinal))
            {
                var value = ValueAfterColon(cleaned).ToLowerInvariant();
                if (Confidences.Contains(value))
                {
                    response.Confidence = value;
                }
            }
            else if (lower.StartsWith("affected database:", StringComparison.Ordinal))
            {
                var value = ValueAfterColon(cleaned);
                if (IsPresent(value))
                {
                    response.AffectedDatabase = value;
                }
            }
            else if (lower.StartsWith("affected query", StringComparison.Ordinal)
                     || lower.StartsWith("affected procedure", StringComparison.Ordinal))
            {
                var value = cleaned.Contains(':', StringComparison.Ordinal)
                    ? ValueAfterColon(cleaned)
                    : string.Empty;

                if (IsPresent(value))
                {
                    response.AffectedQuery = value;
                }
            }
            else if (lower.StartsWith("historical comparison:", StringComparison.Ordinal))
            {
                var value = ValueAfterColon(cleaned);
                if (IsPresent(value))
                {
                    response.HistoricalComparison = value;
                }
            }
            else if (lower.StartsWith("likely cause:", StringComparison.Ordinal))
            {
                var value = ValueAfterColon(cleaned);
                if (value.Length > 0)
                {
                    response.LikelyCause = value;
                }
            }
            else if (lower.StartsWith("human review required:", StringComparison.Ordinal))
            {
                // Anything other than an explicit "no" keeps review required. A model that
                // waffles here must not be able to wave a finding through unreviewed.
                response.HumanReviewRequired =
                    !string.Equals(ValueAfterColon(cleaned), "no", StringComparison.OrdinalIgnoreCase);
            }
        }

        response.Evidence = ExtractBullets(answer, "evidence:");
        response.WhatIsKnown = ExtractBullets(answer, "what is known:");
        response.WhatIsUncertain = ExtractBullets(answer, "what is uncertain:");
        response.RecommendedInvestigation = ExtractBullets(answer, "recommended investigation:");

        ApplyAdaptiveSections(response, answer);
    }

    /// <summary>
    /// Fills whatever the flat "Label: value" pass left unset, reading the adaptive
    /// Markdown report instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent no longer answers with one fixed section list, so a healthy database is
    /// not made to carry the same headings as a live incident. That freedom stops at the
    /// API contract: <c>severity</c>, <c>evidence</c> and the rest must still be
    /// populated, and they can only come from the prose. This pass maps the headings the
    /// prompt offers onto those fields.
    /// </para>
    /// <para>
    /// It runs second and only writes to fields still holding their defaults, so the
    /// older flat format keeps parsing exactly as it did. Where two headings mean the
    /// same thing to a client — "Recommended Investigation" and "Recommended Next Step",
    /// "Likely Cause" and "Assessment" — both are accepted rather than forcing the
    /// report to use wording that does not suit its result.
    /// </para>
    /// </remarks>
    private static void ApplyAdaptiveSections(InvestigationResponse response, string answer)
    {
        var lines = answer.Split('\n');

        if (string.Equals(response.Severity, "unknown", StringComparison.Ordinal))
        {
            // "Status" is the incident severity. "Current Health" only appears on an
            // optimization opportunity, where the health is by definition fine — its
            // sibling "Priority" is deliberately NOT read, because the priority of
            // tuning work must never be reported as the severity of a live incident.
            var status = LastLabelValue(lines, "status:")
                         ?? LastLabelValue(lines, "current health:");

            if (status is not null && StatusSeverities.TryGetValue(status, out var severity))
            {
                response.Severity = severity;
            }
        }

        response.AffectedDatabase ??= SubjectOfReport(lines);
        response.AffectedQuery ??= CandidateName(answer);
        response.HistoricalComparison ??= SectionProse(answer, "Historical Comparison");
        response.LikelyCause ??= SectionProse(answer, "Likely Cause")
                                 ?? SectionProse(answer, "Assessment");

        if (response.Evidence.Count == 0)
        {
            response.Evidence = SectionItems(answer, "Evidence")
                                ?? SectionItems(answer, "Available Evidence")
                                ?? [];
        }

        if (response.WhatIsKnown.Count == 0)
        {
            response.WhatIsKnown = SectionItems(answer, "Successfully Checked") ?? [];
        }

        if (response.WhatIsUncertain.Count == 0)
        {
            response.WhatIsUncertain = SectionItems(answer, "Missing Evidence")
                                       ?? SectionItems(answer, "Unavailable")
                                       ?? [];
        }

        if (SectionItems(answer, "Not Evaluated") is { } notEvaluated)
        {
            response.WhatIsUncertain.AddRange(notEvaluated);
        }

        if (response.RecommendedInvestigation.Count == 0)
        {
            response.RecommendedInvestigation =
                SectionItems(answer, "Recommended Investigation")
                ?? SectionItems(answer, "Recommended Next Steps")
                ?? SectionItems(answer, "Recommended Next Step")
                ?? [];
        }

        // A report whose own heading says the telemetry was missing cannot also hand a
        // client a determinate severity. Models do occasionally write "Historical
        // Analysis Unavailable" above "Status: HIGH", carrying the severity over from a
        // cached statistic they did manage to read. Left alone that reads as a confirmed
        // finding, which is precisely the reading this format exists to prevent.
        //
        // Deliberately limited to the two outcomes that mean "no data". A partial
        // analysis is not clamped: it can still have observed a genuine live incident
        // before a later tool failed, and suppressing that would be the worse error.
        if (OutcomeOfReport(lines) is { } outcome && UnassessableOutcomes.Contains(outcome))
        {
            response.Severity = "unknown";
        }
    }

    /// <summary>Reads the last value written for a flat "Label: value" line.</summary>
    private static string? LastLabelValue(string[] lines, string label)
    {
        string? found = null;

        foreach (var line in lines)
        {
            var cleaned = Emphasis().Replace(line, string.Empty).Trim();

            if (!cleaned.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ValueAfterColon(cleaned);
            if (value.Length > 0)
            {
                found = value.ToLowerInvariant();
            }
        }

        return found;
    }

    /// <summary>Reads the database named in the opening heading, if it names one.</summary>
    /// <remarks>
    /// A report opens "## Merchandising — Performance Issue". Only the very first heading
    /// is considered, so a later "Strongest Candidate — 52 s CPU" cannot be mistaken for
    /// the subject, and a server-wide investigation resolves to no database rather than
    /// to a database called "SQL Server".
    /// </remarks>
    private static string? SubjectOfReport(string[] lines)
    {
        if (FirstReportHeading(lines) is not { } heading)
        {
            return null;
        }

        var subject = heading.Groups["subject"].Value.Trim().Trim('`').Trim();

        return IsPresent(subject) && !ServerSubjects.Contains(subject) ? subject : null;
    }

    /// <summary>Reads the outcome declared in the opening heading.</summary>
    private static string? OutcomeOfReport(string[] lines)
    {
        if (FirstReportHeading(lines) is not { } heading)
        {
            return null;
        }

        var outcome = heading.Groups["outcome"].Value.Trim();

        return outcome.Length > 0 ? outcome : null;
    }

    /// <summary>
    /// Matches the opening "## Subject — Outcome" heading, or returns <c>null</c> when the
    /// report does not start with one.
    /// </summary>
    private static Match? FirstReportHeading(string[] lines)
    {
        foreach (var line in lines)
        {
            var cleaned = Emphasis().Replace(line, string.Empty).Trim();
            if (HeadingName(cleaned) is null)
            {
                continue;
            }

            var match = ReportHeading().Match(cleaned);

            return match.Success ? match : null;
        }

        return null;
    }

    /// <summary>Reads the query or procedure named under the candidate heading.</summary>
    /// <remarks>
    /// The heading holds a name, sometimes trailed by a measurement — "usp_GetOrders —
    /// 52 s average CPU". Only the name belongs in <c>affected_query</c>; the measurement
    /// belongs in the evidence, where it already is.
    /// </remarks>
    private static string? CandidateName(string text)
    {
        var first = SectionItems(text, "Strongest Candidate")?.FirstOrDefault()
                    ?? SectionItems(text, "Candidate")?.FirstOrDefault();

        if (first is null)
        {
            return null;
        }

        var trailing = DescriptionSeparator().Match(first);
        var name = trailing.Success ? first[..trailing.Index] : first;

        name = TrailingParenthetical().Replace(name, string.Empty).Trim().Trim('`').Trim();

        return IsPresent(name) ? name : null;
    }

    /// <summary>
    /// Returns a section's content as a list: its list items, or its prose as a single
    /// item when the section was written as a sentence rather than a list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both "- " and "1." markers are accepted. Models reach for a numbered list whenever
    /// the content is a sequence of steps, which is exactly what a recommendation is, and
    /// treating that as unmarked prose would collapse four distinct steps into one
    /// unreadable run-on item.
    /// </para>
    /// <para>
    /// A line that carries no marker continues the item above it, so an item that wrapped
    /// stays whole. A blank line closes the list, so a trailing note after it is not
    /// silently glued onto the last item.
    /// </para>
    /// <para>
    /// Prose is joined rather than split per line, because a wrapped sentence is one
    /// statement. Returns <c>null</c> — not an empty list — when the heading is absent, so
    /// a caller can fall through to an alternative heading. A heading that is genuinely
    /// empty yields an empty list and stops the search.
    /// </para>
    /// </remarks>
    private static List<string>? SectionItems(string text, string heading)
    {
        var body = SectionBody(text, heading);
        if (body is null)
        {
            return null;
        }

        var items = new List<string>();
        var open = false;

        foreach (var line in body)
        {
            var marker = ListItem().Match(line);

            if (marker.Success)
            {
                items.Add(marker.Groups["text"].Value.Trim());
                open = true;
            }
            else if (line.Length == 0)
            {
                open = false;
            }
            else if (open)
            {
                items[^1] = $"{items[^1]} {line}".Trim();
            }
        }

        items.RemoveAll(item => item.Length == 0);

        if (items.Count > 0)
        {
            return items;
        }

        var prose = string.Join(' ', body.Where(line => line.Length > 0)).Trim();

        return prose.Length > 0 ? [prose] : [];
    }

    /// <summary>Returns a section's content as a single line of prose.</summary>
    private static string? SectionProse(string text, string heading)
    {
        var items = SectionItems(text, heading);
        if (items is null || items.Count == 0)
        {
            return null;
        }

        var prose = string.Join(' ', items).Trim();

        return IsPresent(prose) ? prose : null;
    }

    /// <summary>
    /// Returns the trimmed lines under a Markdown heading, up to the next heading.
    /// </summary>
    private static List<string>? SectionBody(string text, string heading)
    {
        var lines = Emphasis().Replace(text, string.Empty).Split('\n');
        var start = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (HeadingName(lines[i]) is { } name
                && name.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var body = new List<string>();

        for (var i = start; i < lines.Length; i++)
        {
            if (HeadingName(lines[i]) is not null)
            {
                break;
            }

            body.Add(lines[i].Trim());
        }

        return body;
    }

    /// <summary>Returns the title of a Markdown heading line, or <c>null</c> if it is not one.</summary>
    private static string? HeadingName(string line)
    {
        var match = Heading().Match(line.Trim());

        return match.Success ? match.Groups["name"].Value.Trim().TrimEnd(':').Trim() : null;
    }

    /// <summary>Reads the value after the first colon, dropping trailing qualifiers.</summary>
    /// <remarks>
    /// A trailing parenthetical such as "high (based on blocking evidence)" is removed so
    /// the value still matches the allowed set; the reasoning stays in the answer text.
    /// </remarks>
    private static string ValueAfterColon(string line)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return string.Empty;
        }

        var raw = line[(separator + 1)..].Trim();
        raw = TrailingParenthetical().Replace(raw, string.Empty).Trim();

        return raw.TrimEnd('*').Trim();
    }

    private static bool IsPresent(string value) => value.Length > 0 && !Absent.Contains(value);

    /// <summary>
    /// Collects the bullet list under a section header.
    /// Ported from <c>_extract_bullet_section</c>.
    /// </summary>
    /// <remarks>
    /// The section ends at the first blank line after the bullets, or at the next
    /// "Header:" line, so a following section is not absorbed into the list.
    /// </remarks>
    private static List<string> ExtractBullets(string text, string header)
    {
        var plain = Emphasis().Replace(text, string.Empty);
        var index = plain.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return [];
        }

        var items = new List<string>();

        foreach (var line in plain[(index + header.Length)..].Split('\n'))
        {
            var stripped = line.Trim();

            if (stripped.StartsWith("- ", StringComparison.Ordinal)
                || stripped.StartsWith("* ", StringComparison.Ordinal))
            {
                items.Add(stripped[2..].Trim());
            }
            else if (items.Count > 0 && stripped.Length == 0)
            {
                break;
            }
            else if (items.Count > 0
                     && !stripped.StartsWith('-')
                     && !stripped.StartsWith('*')
                     && stripped.Contains(':', StringComparison.Ordinal))
            {
                break;
            }
        }

        return items;
    }

    [GeneratedRegex(@"\*{1,2}")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s*\(.*?\)\s*$")]
    private static partial Regex TrailingParenthetical();

    /// <summary>Any Markdown heading line, capturing its title.</summary>
    [GeneratedRegex(@"^#{1,6}\s+(?<name>.+?)\s*$")]
    private static partial Regex Heading();

    /// <summary>The opening heading, which pairs a subject with an outcome.</summary>
    [GeneratedRegex(@"^#{1,6}\s+(?<subject>.+?)\s+[—–-]\s+(?<outcome>.+?)\s*$")]
    private static partial Regex ReportHeading();

    /// <summary>The dash that separates a named object from a description of it.</summary>
    [GeneratedRegex(@"\s+[—–-]\s+")]
    private static partial Regex DescriptionSeparator();

    /// <summary>A bulleted or numbered list item, capturing its text.</summary>
    [GeneratedRegex(@"^(?:-|\d+[.)])\s+(?<text>.+)$")]
    private static partial Regex ListItem();
}
