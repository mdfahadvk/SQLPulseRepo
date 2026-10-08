using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Services.Agent;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Reading the structured fields back out of the written answer.
/// </summary>
/// <remarks>
/// This parser is what keeps the narrative and the structured data from contradicting each
/// other: both come from the same text. The tests therefore care as much about what is
/// <em>not</em> set on a malformed answer as about what is — an unparsed field must fall
/// back to a conservative default rather than to an unearned claim.
/// </remarks>
public sealed class InvestigationAnswerParserTests
{
    private static InvestigationResponse Parse(string answer)
    {
        var response = new InvestigationResponse { Answer = answer };
        InvestigationAnswerParser.Apply(response, answer);
        return response;
    }

    // --- Severity and confidence ---------------------------------------------

    [Theory]
    [InlineData("Severity: critical", "critical")]
    [InlineData("Severity: HIGH", "high")]
    [InlineData("**Severity:** medium", "medium")]
    [InlineData("Severity: low", "low")]
    public void Severity_is_read_from_the_answer(string line, string expected)
    {
        Assert.Equal(expected, Parse(line).Severity);
    }

    [Fact]
    public void Severity_outside_the_allowed_set_is_ignored()
    {
        // A model inventing its own scale must not be able to introduce a severity the
        // rest of SQLPulse cannot interpret.
        Assert.Equal("unknown", Parse("Severity: catastrophic").Severity);
    }

    [Theory]
    [InlineData("Confidence: high", "high")]
    [InlineData("Confidence: Medium (three tools agreed)", "medium")]
    public void Confidence_is_read_from_the_answer(string line, string expected)
    {
        Assert.Equal(expected, Parse(line).Confidence);
    }

    [Fact]
    public void Confidence_defaults_to_low_when_the_answer_omits_it()
    {
        Assert.Equal("low", Parse("Nothing structured here.").Confidence);
    }

    [Fact]
    public void A_trailing_qualifier_does_not_prevent_a_value_from_matching()
    {
        // Models routinely justify the rating inline. The reasoning stays in the answer
        // text; only the value is extracted.
        Assert.Equal("high", Parse("Severity: high (blocking chain is growing)").Severity);
    }

    [Fact]
    public void The_last_declaration_of_a_field_wins()
    {
        var response = Parse("Severity: low\nSeverity: critical");

        Assert.Equal("critical", response.Severity);
    }

    // --- Affected objects -----------------------------------------------------

    [Fact]
    public void Affected_database_is_read_from_the_answer()
    {
        Assert.Equal("Merchandising", Parse("Affected Database: Merchandising").AffectedDatabase);
    }

    [Theory]
    [InlineData("Affected Database: N/A")]
    [InlineData("Affected Database: none")]
    [InlineData("Affected Database:")]
    public void A_written_out_absent_value_leaves_the_field_unset(string line)
    {
        // "N/A" is the model saying it does not know, not a database called N/A.
        Assert.Null(Parse(line).AffectedDatabase);
    }

    [Theory]
    [InlineData("Affected Query: usp_GetOrders")]
    [InlineData("Affected Query / Procedure: usp_GetOrders")]
    [InlineData("Affected Procedure: usp_GetOrders")]
    public void Affected_query_is_read_under_either_heading(string line)
    {
        Assert.Equal("usp_GetOrders", Parse(line).AffectedQuery);
    }

    [Fact]
    public void Historical_comparison_and_likely_cause_are_read_from_the_answer()
    {
        var response = Parse(
            """
            Historical Comparison: 4x slower than the 7-day baseline
            Likely Cause: missing index on Orders.CustomerId
            """);

        Assert.Equal("4x slower than the 7-day baseline", response.HistoricalComparison);
        Assert.Equal("missing index on Orders.CustomerId", response.LikelyCause);
    }

    // --- Human review ---------------------------------------------------------

    [Fact]
    public void Human_review_is_cleared_only_by_an_explicit_no()
    {
        Assert.False(Parse("Human Review Required: No").HumanReviewRequired);
    }

    [Theory]
    [InlineData("Human Review Required: Yes")]
    [InlineData("Human Review Required: probably not")]
    [InlineData("Human Review Required:")]
    public void Anything_other_than_no_keeps_human_review_required(string line)
    {
        // A model that waffles must not be able to wave a finding through unreviewed.
        Assert.True(Parse(line).HumanReviewRequired);
    }

    [Fact]
    public void Human_review_is_required_when_the_answer_says_nothing_about_it()
    {
        Assert.True(Parse("Severity: low").HumanReviewRequired);
    }

    // --- Bullet sections ------------------------------------------------------

    [Fact]
    public void Bullet_sections_are_collected_separately()
    {
        var response = Parse(
            """
            Answer: Session 71 is blocking four others.

            Evidence:
            - Session 71 holds an exclusive lock on dbo.Orders
            - Four sessions are waiting on LCK_M_X

            What Is Known:
            - The blocker has been open for 210 seconds

            What Is Uncertain:
            - Whether the application will retry

            Recommended Investigation:
            - Identify the transaction owner
            - Review the application's transaction scope
            """);

        Assert.Equal(2, response.Evidence.Count);
        Assert.Equal("Session 71 holds an exclusive lock on dbo.Orders", response.Evidence[0]);
        Assert.Equal(new[] { "The blocker has been open for 210 seconds" }, response.WhatIsKnown);
        Assert.Equal(new[] { "Whether the application will retry" }, response.WhatIsUncertain);
        Assert.Equal(2, response.RecommendedInvestigation.Count);
    }

    [Fact]
    public void A_bullet_section_ends_at_the_next_header_without_a_blank_line()
    {
        var response = Parse(
            """
            Evidence:
            - avg CPU 4200 ms
            Likely Cause: a plan regression
            """);

        Assert.Equal(new[] { "avg CPU 4200 ms" }, response.Evidence);
    }

    [Fact]
    public void Bullet_sections_survive_markdown_emphasis()
    {
        var response = Parse(
            """
            **Evidence:**
            - **Session 71** holds a lock
            """);

        Assert.Equal(new[] { "Session 71 holds a lock" }, response.Evidence);
    }

    [Fact]
    public void A_missing_section_yields_an_empty_list_rather_than_a_failure()
    {
        var response = Parse("Severity: low");

        Assert.Empty(response.Evidence);
        Assert.Empty(response.WhatIsKnown);
        Assert.Empty(response.WhatIsUncertain);
        Assert.Empty(response.RecommendedInvestigation);
    }

    [Fact]
    public void Asterisk_bullets_are_not_collected()
    {
        // Emphasis is stripped from the whole answer before bullets are matched, so a
        // "* " bullet has already become plain text by then. This mirrors the Python
        // reference exactly; changing it would change what existing clients receive.
        var response = Parse(
            """
            Evidence:
            * avg CPU 4200 ms
            """);

        Assert.Empty(response.Evidence);
    }

    // --- Defaults -------------------------------------------------------------

    [Fact]
    public void An_empty_answer_leaves_every_default_intact()
    {
        var response = new InvestigationResponse
        {
            Severity = "high",
            Confidence = "high",
            AffectedDatabase = "AppDb",
        };

        InvestigationAnswerParser.Apply(response, string.Empty);

        Assert.Equal("high", response.Severity);
        Assert.Equal("high", response.Confidence);
        Assert.Equal("AppDb", response.AffectedDatabase);
    }

    [Fact]
    public void Free_prose_is_parsed_without_error_and_claims_nothing()
    {
        // The common failure mode is a model ignoring the template. That must degrade to
        // "we do not know", never to a confident-looking but unsupported result.
        var response = Parse("The Merchandising database looks healthy right now.");

        Assert.Equal("unknown", response.Severity);
        Assert.Equal("low", response.Confidence);
        Assert.Null(response.AffectedDatabase);
        Assert.True(response.HumanReviewRequired);
        Assert.Empty(response.Evidence);
    }
}
