using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Services.Agent;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The adaptive investigation report: one contract, several shapes.
/// </summary>
/// <remarks>
/// <para>
/// The agent no longer answers every question with the same section list, because a
/// healthy database forced through an incident template produces a page of "N/A" that
/// buries the one line a reader wanted. Each shape here is a result a reader would
/// recognise — an incident, an opportunity, a clean bill of health, absent telemetry —
/// and each must still populate the same structured fields the API promises.
/// </para>
/// <para>
/// These tests therefore assert two things at once: that the readable report is allowed
/// to differ, and that the JSON derived from it does not. The failure they exist to catch
/// is a heading being reworded in the prompt while the parser goes on looking for the old
/// one, which does not break anything loudly — it just empties a field.
/// </para>
/// </remarks>
public sealed class AdaptiveInvestigationReportTests
{
    private static InvestigationResponse Parse(string answer)
    {
        var response = new InvestigationResponse { Answer = answer };
        InvestigationAnswerParser.Apply(response, answer);
        return response;
    }

    // --- Healthy --------------------------------------------------------------

    private const string HealthyReport =
        """
        ## Merchandising — Healthy

        **Status:** HEALTHY
        **Confidence:** High

        No significant current performance issue was detected.

        ### Evidence
        - Active requests: 0
        - Blocking chains: 0
        - Long-running queries: 0
        - No significant CPU or I/O concern detected

        ### Agent Action
        Analysis only — no database changes were performed.
        Human review required: no
        """;

    [Fact]
    public void A_healthy_database_reports_a_low_severity_rather_than_an_unknown_one()
    {
        // "Healthy" is a finding, not an absence of one. Mapping it to unknown would make
        // a clean result indistinguishable from a database nobody managed to examine.
        var response = Parse(HealthyReport);

        Assert.Equal("low", response.Severity);
        Assert.Equal("high", response.Confidence);
        Assert.Equal("Merchandising", response.AffectedDatabase);
        Assert.False(response.HumanReviewRequired);
    }

    [Fact]
    public void A_healthy_report_still_carries_its_evidence()
    {
        Assert.Equal(
            new[]
            {
                "Active requests: 0",
                "Blocking chains: 0",
                "Long-running queries: 0",
                "No significant CPU or I/O concern detected",
            },
            Parse(HealthyReport).Evidence);
    }

    [Fact]
    public void A_simple_result_leaves_inapplicable_fields_empty_instead_of_saying_N_A()
    {
        // The whole point of the adaptive format: a healthy database has no affected
        // query and no baseline to compare against, so those fields are absent rather
        // than filled with a placeholder the client would have to special-case.
        var response = Parse(HealthyReport);

        Assert.Null(response.AffectedQuery);
        Assert.Null(response.HistoricalComparison);
        Assert.Null(response.LikelyCause);
        Assert.Empty(response.WhatIsUncertain);
        Assert.Empty(response.RecommendedInvestigation);
        Assert.DoesNotContain("N/A", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_healthy_report_may_still_name_an_optimization_opportunity()
    {
        var response = Parse(
            """
            ## Merchandising — Healthy

            **Status:** HEALTHY
            **Confidence:** High

            ### Evidence
            - Active requests: 0
            - Blocking chains: 0

            ### Optimization Opportunities
            - uspMerch_GetLTL has averaged 52.4 s CPU across 1,204 cached executions

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: no
            """);

        // The opportunity must not be absorbed into the evidence for the health verdict,
        // nor promote a healthy database into an incident.
        Assert.Equal(2, response.Evidence.Count);
        Assert.Equal("low", response.Severity);
    }

    // --- Current incident -----------------------------------------------------

    private const string BlockingIncidentReport =
        """
        ## AppDb — Performance Issue

        **Status:** HIGH
        **Confidence:** Medium

        ### Summary
        Session 71 heads a blocking chain holding four sessions on dbo.Orders.

        ### Strongest Candidate
        usp_GetOrders

        ### Evidence
        - Session 71 has held an exclusive lock on dbo.Orders for 210 seconds
        - 4 sessions are waiting on LCK_M_X

        ### Historical Comparison
        Maximum duration 42.6 s against a historical P95 of 7.1 s.

        ### Likely Cause
        An application transaction left open across a round trip. Not proven.

        ### Recommended Investigation
        - Identify the owner of session 71
        - Review the transaction scope in the calling application

        ### Agent Action
        Analysis only — no database changes were performed.
        Human review required: yes (a blocking chain needs an owner decision)
        """;

    [Fact]
    public void A_current_incident_fills_the_full_structured_contract()
    {
        var response = Parse(BlockingIncidentReport);

        Assert.Equal("high", response.Severity);
        Assert.Equal("medium", response.Confidence);
        Assert.Equal("AppDb", response.AffectedDatabase);
        Assert.Equal("usp_GetOrders", response.AffectedQuery);
        Assert.Equal(2, response.Evidence.Count);
        Assert.Equal(
            "Maximum duration 42.6 s against a historical P95 of 7.1 s.",
            response.HistoricalComparison);
        Assert.StartsWith("An application transaction", response.LikelyCause, StringComparison.Ordinal);
        Assert.Equal(2, response.RecommendedInvestigation.Count);
        Assert.True(response.HumanReviewRequired);
    }

    // --- Optimization opportunity ---------------------------------------------

    [Fact]
    public void An_optimization_opportunity_is_not_reported_as_a_live_incident()
    {
        // The trap this guards: a "Priority: High" on tuning work being read as the
        // severity of a current outage. Severity follows the health of the database,
        // which is stated separately and is fine.
        var response = Parse(
            """
            ## Merchandising — Optimization Opportunity

            **Current Health:** HEALTHY
            **Priority:** High
            **Confidence:** Medium

            No active incident is currently detected, but SQLPulse identified an
            optimization opportunity.

            ### Candidate
            uspMerch_GetLTLCalculationDetails_Bulk

            ### Evidence
            - Average CPU 52.4 s across 1,204 cached executions
            - Logical reads 2.4M per execution

            ### Why It Matters
            Cumulative plan-cache cost is the largest on the instance.

            ### Recommended Investigation
            - Review the execution plan for scan-heavy operators

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes (indexing changes need DBA approval)
            """);

        Assert.Equal("low", response.Severity);
        Assert.Equal("medium", response.Confidence);
        Assert.Equal("Merchandising", response.AffectedDatabase);
        Assert.Equal("uspMerch_GetLTLCalculationDetails_Bulk", response.AffectedQuery);
        Assert.Equal(2, response.Evidence.Count);
        Assert.True(response.HumanReviewRequired);
    }

    // --- Historical telemetry unavailable --------------------------------------

    private const string QueryStoreDisabledReport =
        """
        ## Merchandising — Historical Analysis Unavailable

        **Status:** DATA UNAVAILABLE
        **Confidence:** High

        SQLPulse could not analyze historical slow-query performance for 2026-09-09
        because Query Store is disabled. This does not indicate a performance problem.
        It means the required historical telemetry is unavailable.

        ### Evidence
        - Query Store: DISABLED
        - Candidates: 0

        ### Recommended Next Step
        Consider enabling Query Store after DBA review if historical query analysis is
        required.

        ### Agent Action
        Analysis only — no database changes were performed.
        Human review required: yes (enabling Query Store is a configuration change)
        """;

    [Fact]
    public void Absent_history_is_reported_as_unknown_rather_than_as_health()
    {
        // Zero candidates from a Query Store that was never running is not evidence that
        // nothing ran slowly. Mapping this to "low" would tell a client the database was
        // examined and found clean, which is the opposite of what happened.
        var response = Parse(QueryStoreDisabledReport);

        Assert.Equal("unknown", response.Severity);
        Assert.Equal("high", response.Confidence);
        Assert.Equal("Merchandising", response.AffectedDatabase);
    }

    [Fact]
    public void Absent_history_states_plainly_that_it_is_not_a_performance_problem()
    {
        var response = Parse(QueryStoreDisabledReport);

        Assert.Contains(
            "does not indicate a performance problem",
            response.Answer,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("no slow queries existed", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.AffectedQuery);
    }

    [Fact]
    public void A_single_next_step_written_as_prose_still_reaches_the_client()
    {
        // Forcing a one-line recommendation into a bullet list is the kind of padding
        // this format exists to remove, so the prose form has to parse too — and a
        // sentence that wrapped across two lines must stay one recommendation.
        Assert.Equal(
            new[]
            {
                "Consider enabling Query Store after DBA review if historical query analysis is required.",
            },
            Parse(QueryStoreDisabledReport).RecommendedInvestigation);
    }

    [Fact]
    public void A_recommendation_that_changes_configuration_requires_human_review()
    {
        // No performance finding needed a decision here. The recommendation does, because
        // enabling Query Store changes database configuration.
        Assert.True(Parse(QueryStoreDisabledReport).HumanReviewRequired);
    }

    // --- List shapes the model actually writes ---------------------------------

    [Fact]
    public void Numbered_steps_are_read_as_separate_recommendations()
    {
        // Observed against the live provider: a model asked for next steps writes them as
        // a numbered list, not bullets. Reading that as unmarked prose collapsed four
        // distinct steps into one run-on string.
        var response = Parse(
            """
            ## Merchandising — Optimization Opportunity

            **Current Health:** HEALTHY
            **Confidence:** Medium

            ### Recommended Investigation
            1. Review the execution plan for costly operators
            2. Examine indexes on the referenced tables
            3. Confirm whether the cost persists under current workload

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes
            """);

        Assert.Equal(
            new[]
            {
                "Review the execution plan for costly operators",
                "Examine indexes on the referenced tables",
                "Confirm whether the cost persists under current workload",
            },
            response.RecommendedInvestigation);
    }

    [Fact]
    public void A_list_item_that_wrapped_across_lines_stays_one_item()
    {
        var response = Parse(
            """
            ## AppDb — Performance Issue

            **Status:** HIGH
            **Confidence:** Medium

            ### Evidence
            - Average CPU per execution 52,348 ms across 8 executions, against a
              configured threshold of 30,000 ms
            - Average logical reads 493,643 per execution

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes
            """);

        Assert.Equal(2, response.Evidence.Count);
        Assert.Equal(
            "Average CPU per execution 52,348 ms across 8 executions, against a configured threshold of 30,000 ms",
            response.Evidence[0]);
    }

    [Fact]
    public void A_note_after_a_blank_line_is_not_glued_onto_the_last_item()
    {
        var response = Parse(
            """
            ## AppDb — Healthy

            **Status:** HEALTHY
            **Confidence:** High

            ### Evidence
            - Active requests: 0
            - Blocking chains: 0

            All six current-performance tools returned clean.
            """);

        Assert.Equal(new[] { "Active requests: 0", "Blocking chains: 0" }, response.Evidence);
    }

    // --- Insufficient data ----------------------------------------------------

    [Fact]
    public void A_thin_baseline_is_neither_healthy_nor_a_finding()
    {
        var response = Parse(
            """
            ## Sales — Insufficient Historical Data

            **Status:** INSUFFICIENT DATA
            **Confidence:** Low

            SQLPulse has telemetry for this window but not enough samples to establish a
            reliable baseline.

            ### Available Evidence
            - 3 executions recorded in the last 7 days

            ### Missing Evidence
            - Baseline sample count insufficient (3 of 5 required)
            - Historical P95 unavailable

            ### Recommended Next Step
            Continue collecting telemetry and reassess when sufficient history exists.

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: no
            """);

        Assert.Equal("unknown", response.Severity);
        Assert.Equal("low", response.Confidence);
        Assert.Equal(new[] { "3 executions recorded in the last 7 days" }, response.Evidence);
        Assert.Equal(2, response.WhatIsUncertain.Count);
        Assert.Single(response.RecommendedInvestigation);
    }

    // --- Partial analysis -----------------------------------------------------

    [Fact]
    public void A_partial_analysis_separates_what_was_checked_from_what_was_not()
    {
        var response = Parse(
            """
            ## Merchandising — Partial Analysis

            **Status:** PARTIAL ANALYSIS
            **Confidence:** Low

            ### Successfully Checked
            - Active requests
            - Blocking
            - CPU

            ### Not Evaluated
            - Application timeout classification: Not evaluated — ApplicationTimeoutMs is not configured.

            ### Unavailable
            - Query Store — permission denied
            - Wait statistics — insufficient permission

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes (a permission grant needs review)
            """);

        Assert.Equal("unknown", response.Severity);
        Assert.Equal("low", response.Confidence);
        Assert.Equal(new[] { "Active requests", "Blocking", "CPU" }, response.WhatIsKnown);
        Assert.Equal(3, response.WhatIsUncertain.Count);
        Assert.Contains(
            response.WhatIsUncertain,
            item => item.Contains("ApplicationTimeoutMs is not configured", StringComparison.Ordinal));
        Assert.Contains("permission denied", response.WhatIsUncertain[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_wide_report_resolves_to_no_affected_database()
    {
        // "SQL Server" in the heading names the instance, not a database called
        // "SQL Server". Recording it as one would send a client looking for it.
        var response = Parse(
            """
            ## SQL Server — Partial Analysis

            **Status:** PARTIAL ANALYSIS
            **Confidence:** Low

            ### Unavailable
            - Wait statistics — insufficient permission
            """);

        Assert.Null(response.AffectedDatabase);
    }

    // --- Historical finding ---------------------------------------------------

    [Fact]
    public void A_historical_slow_query_is_named_without_its_measurement()
    {
        // The candidate heading carries a name and often a figure. Only the name is an
        // identifier; the figure is evidence and already appears as such.
        var response = Parse(
            """
            ## Merchandising — Performance Issue

            **Status:** MEDIUM
            **Confidence:** Medium

            ### Summary
            One query exceeded the slow-query monitoring threshold yesterday.

            ### Strongest Candidate
            `uspMerch_RecalculateFreight` — 41.2 s maximum duration

            ### Evidence
            - Maximum duration: 41.2 s
            - Executions: 18
            - Classification: slow_query_candidate

            ### Assessment
            Approximately 6x the historical P95 and a strong investigation candidate.

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes
            """);

        Assert.Equal("medium", response.Severity);
        Assert.Equal("uspMerch_RecalculateFreight", response.AffectedQuery);
        Assert.Equal(3, response.Evidence.Count);
        Assert.Equal(
            "Approximately 6x the historical P95 and a strong investigation candidate.",
            response.LikelyCause);
    }

    // --- Consistency ----------------------------------------------------------

    [Theory]
    [InlineData("Historical Analysis Unavailable")]
    [InlineData("Insufficient Historical Data")]
    public void A_report_that_lacked_telemetry_cannot_also_declare_a_severity(string outcome)
    {
        // Observed against the live provider: the model carried a severity over from the
        // one cached statistic it could read, and stated it above a heading that admitted
        // the telemetry was missing. That reads to a client as a confirmed finding.
        var response = Parse(
            $"""
            ## Merchandising — {outcome}

            **Status:** HIGH
            **Confidence:** Medium

            ### Evidence
            - Query Store: DISABLED

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes
            """);

        Assert.Equal("unknown", response.Severity);
    }

    [Fact]
    public void A_partial_analysis_may_still_report_a_live_incident_it_did_observe()
    {
        // The clamp above must not extend to a partial analysis. A tool failing late does
        // not undo a blocking chain the earlier tools actually saw, and hiding it would
        // be the more dangerous error.
        var response = Parse(
            """
            ## AppDb — Partial Analysis

            **Status:** HIGH
            **Confidence:** Low

            ### Successfully Checked
            - Blocking

            ### Unavailable
            - Wait statistics — insufficient permission

            ### Agent Action
            Analysis only — no database changes were performed.
            Human review required: yes
            """);

        Assert.Equal("high", response.Severity);
    }

    [Theory]
    [InlineData(HealthyReport, "low", "high")]
    [InlineData(BlockingIncidentReport, "high", "medium")]
    [InlineData(QueryStoreDisabledReport, "unknown", "high")]
    public void The_structured_fields_never_contradict_the_written_report(
        string report,
        string severity,
        string confidence)
    {
        var response = Parse(report);

        Assert.Equal(severity, response.Severity);
        Assert.Equal(confidence, response.Confidence);
    }

    [Fact]
    public void An_adaptive_report_never_claims_the_agent_changed_anything()
    {
        foreach (var report in new[] { HealthyReport, BlockingIncidentReport, QueryStoreDisabledReport })
        {
            Assert.Contains("no database changes were performed", report, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Prompt and parser must agree ------------------------------------------

    [Theory]
    [InlineData("### Evidence")]
    [InlineData("### Available Evidence")]
    [InlineData("### Missing Evidence")]
    [InlineData("### Successfully Checked")]
    [InlineData("### Not Evaluated")]
    [InlineData("### Unavailable")]
    [InlineData("### Historical Comparison")]
    [InlineData("### Likely Cause")]
    [InlineData("### Assessment")]
    [InlineData("### Strongest Candidate")]
    [InlineData("### Candidate")]
    [InlineData("### Recommended Investigation")]
    [InlineData("### Recommended Next Step")]
    [InlineData("### Agent Action")]
    [InlineData("Human review required:")]
    public void Every_heading_the_parser_reads_is_one_the_prompt_offers(string heading)
    {
        // Reword a heading in the prompt without telling the parser and nothing fails
        // loudly — the matching JSON field just goes quietly empty. This is the test that
        // makes that mistake visible.
        Assert.Contains(heading, AgentPrompt.Build(DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("**Status:**")]
    [InlineData("**Current Health:**")]
    [InlineData("**Confidence:**")]
    public void Every_header_line_the_parser_reads_is_one_the_prompt_offers(string label)
    {
        Assert.Contains(label, AgentPrompt.Build(DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_offers_every_result_shape_the_parser_can_classify()
    {
        var prompt = AgentPrompt.Build(DateTimeOffset.UtcNow);

        Assert.Contains("HEALTHY", prompt, StringComparison.Ordinal);
        Assert.Contains("DATA UNAVAILABLE", prompt, StringComparison.Ordinal);
        Assert.Contains("INSUFFICIENT DATA", prompt, StringComparison.Ordinal);
        Assert.Contains("PARTIAL ANALYSIS", prompt, StringComparison.Ordinal);
        Assert.Contains("Optimization Opportunity", prompt, StringComparison.Ordinal);
        Assert.Contains("Performance Issue", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_forbids_padding_a_report_with_placeholders()
    {
        Assert.Contains(
            """never pad a section""",
            AgentPrompt.Build(DateTimeOffset.UtcNow),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_refuses_to_read_absent_telemetry_as_an_absence_of_slow_queries()
    {
        Assert.Contains(
            """NEVER means "no slow queries existed""",
            AgentPrompt.Build(DateTimeOffset.UtcNow),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_keeps_configuration_changes_behind_a_dba_decision()
    {
        var prompt = AgentPrompt.Build(DateTimeOffset.UtcNow);

        Assert.Contains("after DBA review", prompt, StringComparison.Ordinal);
        Assert.Contains("SQLPulse never enables Query Store", prompt, StringComparison.Ordinal);
        Assert.Contains("never imply that you will act", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_refuses_to_read_cached_cost_as_a_live_incident()
    {
        // The observed failure this guards: an expensive plan-cache entry being reported
        // as a HIGH status while nothing at all was running.
        Assert.Contains(
            "NEVER justifies a CRITICAL, HIGH or MEDIUM status",
            AgentPrompt.Build(DateTimeOffset.UtcNow),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_requires_the_status_to_match_the_heading()
    {
        Assert.Contains(
            """takes "Status: PARTIAL ANALYSIS", never HIGH""",
            AgentPrompt.Build(DateTimeOffset.UtcNow),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_bounds_the_report_so_it_always_reaches_its_closing_block()
    {
        // Without a budget the model spends its completion allowance on optional sections
        // and is cut off mid-sentence, losing Agent Action and the human-review answer.
        var prompt = AgentPrompt.Build(DateTimeOffset.UtcNow);

        Assert.Contains("at most six sections", prompt, StringComparison.Ordinal);
        Assert.Contains("at most two sentences", prompt, StringComparison.Ordinal);
        Assert.Contains("at most five bullets", prompt, StringComparison.Ordinal);
        Assert.Contains("at most three items", prompt, StringComparison.Ordinal);
        Assert.Contains("never finish without Agent Action", prompt, StringComparison.Ordinal);
    }
}
