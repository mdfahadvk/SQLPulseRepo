"""Tests for slow-query vs timeout classification and investigation breadth.

Two conflations are being guarded against here:

  1. A monitoring threshold is not an application timeout.  Whoever configures
     monitoring picks an arbitrary "show me anything over N ms"; only the
     application knows the duration at which its client gives up.  Treating the
     first as the second manufactures timeout incidents that never happened.

  2. A naming convention is not evidence of who called a procedure.  "usp" is
     written by application developers and DBAs alike, so deciding workload
     from it produces a confident label backed by nothing.

Plus the investigation-breadth rules: a broad "what is affecting X right now"
question must look at CPU and I/O before concluding anything, and must keep
a current incident distinct from an optimization opportunity.
"""

import pytest

from app.agent.engine import SYSTEM_PROMPT, investigate
from app.detectors.timeout_candidate import (
    build_timeout_candidates,
    classify_candidate,
    classify_workload,
)
from app.models.findings import FindingCategory
from app.models.slow_history import (
    CandidateClassification,
    SlowQueryRecord,
    WorkloadType,
)

from tests.test_agent import (
    MockLLMProvider,
    _make_final_response,
    _make_tool_call_response,
)


def _record(
    max_duration_ms: float,
    *,
    query_hash: str = "0xABC",
    query_id: int = 1,
    sql_text: str = "SELECT * FROM dbo.Orders WHERE Id = @p0",
    procedure_name: str | None = None,
    program_name: str | None = None,
    is_scheduled_job: bool | None = None,
) -> SlowQueryRecord:
    return SlowQueryRecord(
        query_id=query_id,
        query_hash=query_hash,
        plan_id=10,
        object_id=0,
        query_sql_text=sql_text,
        procedure_name=procedure_name,
        program_name=program_name,
        is_scheduled_job=is_scheduled_job,
        execution_count=8,
        avg_duration_ms=max_duration_ms * 0.6,
        max_duration_ms=max_duration_ms,
        min_duration_ms=max_duration_ms * 0.2,
        avg_cpu_time_ms=500.0,
        max_cpu_time_ms=900.0,
        avg_logical_reads=1_000.0,
        max_logical_reads=3_000.0,
    )


# ─── 1. Slow threshold must not become a timeout threshold ────────────────


class TestSlowThresholdIsNotTimeoutThreshold:
    """A 5s monitoring threshold must never imply a 30s application timeout."""

    def test_the_worked_example(self):
        """Monitoring 5s, app timeout 30s, observed 9,749 ms.

        This is the exact case that was previously misreported: the query is
        genuinely slow but nowhere near the timeout, so calling it a timeout
        candidate would invent an incident the application never saw.
        """
        candidates = build_timeout_candidates(
            [_record(9_749.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )

        assert len(candidates) == 1
        candidate = candidates[0]
        assert candidate.slow_query_candidate is True
        assert candidate.timeout_candidate is False
        assert candidate.classification is CandidateClassification.SLOW_QUERY_CANDIDATE
        assert candidate.slow_threshold_ms == 5_000.0
        assert candidate.application_timeout_ms == 30_000.0

    def test_no_configured_timeout_never_promotes(self):
        """Unknown application timeout means the question stays unanswered.

        Falling back to the monitoring threshold here would make every slow
        query a timeout candidate by construction.
        """
        candidates = build_timeout_candidates(
            [_record(60_000.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=None,
        )

        candidate = candidates[0]
        assert candidate.timeout_candidate is False
        assert candidate.classification is CandidateClassification.SLOW_QUERY_CANDIDATE
        assert candidate.application_timeout_ms is None
        assert "no application timeout is configured" in (
            candidate.classification_reason.lower()
        )

    def test_classification_reason_names_the_threshold_used(self):
        _, reason = classify_candidate(9_749.0, 5_000.0, 30_000.0)
        assert "5s slow-query monitoring threshold" in reason
        assert "30s application timeout" in reason

    def test_finding_wording_does_not_mention_a_timeout(self):
        """The finding text must not claim a timeout threshold was exceeded."""
        from app.detectors.timeout_candidate import build_timeout_findings

        candidates = build_timeout_candidates(
            [_record(9_749.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )
        findings = build_timeout_findings(
            candidates, 5_000.0, "Merchandising", "srv",
        )

        assert findings[0].category == FindingCategory.SLOW_QUERY_CANDIDATE
        assert "slow-query monitoring threshold" in findings[0].summary
        assert "application timeout" not in findings[0].summary.lower()

    def test_below_slow_threshold_is_not_reported_at_all(self):
        candidates = build_timeout_candidates(
            [_record(4_999.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )
        assert candidates == []


# ─── 2. Explicit application timeout exceeded ─────────────────────────────


class TestExplicitApplicationTimeoutExceeded:
    def test_exceeding_configured_timeout_is_a_timeout_candidate(self):
        candidates = build_timeout_candidates(
            [_record(31_200.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )

        candidate = candidates[0]
        assert candidate.timeout_candidate is True
        assert candidate.slow_query_candidate is True, (
            "a timeout candidate is still slow — the two are not exclusive"
        )
        assert candidate.classification is CandidateClassification.TIMEOUT_CANDIDATE
        assert candidate.label == "timeout_candidate"
        assert "30s application timeout" in candidate.classification_reason

    def test_exactly_at_the_timeout_counts(self):
        """A client timing out at exactly its limit is the expected case."""
        classification, _ = classify_candidate(30_000.0, 5_000.0, 30_000.0)
        assert classification is CandidateClassification.TIMEOUT_CANDIDATE

    def test_one_millisecond_below_the_timeout_does_not(self):
        classification, _ = classify_candidate(29_999.0, 5_000.0, 30_000.0)
        assert classification is CandidateClassification.SLOW_QUERY_CANDIDATE

    def test_finding_category_and_wording_reflect_the_timeout(self):
        from app.detectors.timeout_candidate import build_timeout_findings

        candidates = build_timeout_candidates(
            [_record(31_200.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )
        findings = build_timeout_findings(
            candidates, 5_000.0, "Merchandising", "srv",
        )

        assert findings[0].category == FindingCategory.TIMEOUT_CANDIDATE
        assert "application timeout" in findings[0].summary.lower()

    def test_mixed_batch_classifies_each_query_independently(self):
        candidates = build_timeout_candidates(
            [
                _record(9_749.0, query_hash="0xSLOW", query_id=1),
                _record(45_000.0, query_hash="0xTIMEOUT", query_id=2),
            ],
            threshold_ms=5_000.0,
            database="Merchandising",
            application_timeout_ms=30_000.0,
        )

        by_hash = {c.query_hash: c for c in candidates}
        assert by_hash["0xSLOW"].timeout_candidate is False
        assert by_hash["0xTIMEOUT"].timeout_candidate is True


# ─── 3. "usp" naming alone does not prove application workload ────────────


class TestProcedureNamingIsWeakEvidence:
    def test_usp_prefix_alone_returns_unknown(self):
        workload, evidence = classify_workload(None, "dbo.uspMerch_GetOrders")

        assert workload == WorkloadType.UNKNOWN
        assert evidence, "the weak signal should still be reported"
        assert all(not e.startswith("strong:") for e in evidence)

    def test_usp_prefix_with_sql_still_returns_unknown(self):
        """Adding a generic SELECT does not strengthen a naming convention."""
        workload, _ = classify_workload(
            "SELECT * FROM dbo.Orders WHERE Id = @p0",
            "dbo.uspMerch_GetOrders",
        )
        assert workload == WorkloadType.UNKNOWN

    def test_usp_named_maintenance_procedure_is_not_application(self):
        """The case the naming heuristic gets exactly backwards.

        A DBA's index-rebuild helper follows the 'usp' convention while being
        pure maintenance, so naming would have labelled it application work.
        """
        workload, _ = classify_workload(
            "ALTER INDEX ALL ON dbo.Orders REBUILD",
            "dbo.uspRebuildIndexes",
        )
        assert workload == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_naming_evidence_is_labelled_as_a_convention(self):
        _, evidence = classify_workload(None, "dbo.uspMerch_GetOrders")
        assert any("convention" in e.lower() for e in evidence)

    def test_program_name_overrides_the_naming_hint(self):
        """Real caller evidence resolves what naming could not."""
        workload, evidence = classify_workload(
            None,
            "dbo.uspMerch_GetOrders",
            program_name="SQLAgent - TSQL JobStep (Job 0x9A : Step 2)",
        )
        assert workload == WorkloadType.BACKGROUND_OR_SCHEDULED
        assert any(e.startswith("strong:") for e in evidence)

    def test_candidate_from_usp_procedure_is_unknown_workload(self):
        candidates = build_timeout_candidates(
            [_record(9_749.0, procedure_name="dbo.uspMerch_GetOrders")],
            threshold_ms=5_000.0,
            database="Merchandising",
        )
        assert candidates[0].workload_type == WorkloadType.UNKNOWN


# ─── 4. Insufficient evidence returns UNKNOWN ─────────────────────────────


class TestInsufficientWorkloadEvidence:
    def test_no_evidence_at_all_returns_unknown(self):
        workload, evidence = classify_workload(None, None)
        assert workload == WorkloadType.UNKNOWN
        assert evidence

    def test_generic_sql_only_returns_unknown(self):
        for sql in (
            "SELECT * FROM dbo.Orders",
            "INSERT INTO dbo.Audit (Message) VALUES (@m)",
            "UPDATE dbo.Orders SET Status = @s WHERE Id = @id",
            "DELETE FROM dbo.Cart WHERE Expired = 1",
        ):
            workload, _ = classify_workload(sql, None)
            assert workload == WorkloadType.UNKNOWN, sql

    def test_unrecognised_program_name_returns_unknown(self):
        """An unfamiliar program name is not evidence in either direction."""
        workload, evidence = classify_workload(
            "SELECT * FROM dbo.Orders",
            None,
            program_name="internal-tool-7",
        )
        assert workload == WorkloadType.UNKNOWN
        assert any("not a recognised" in e for e in evidence)

    def test_unknown_reports_why_it_is_unknown(self):
        _, evidence = classify_workload("SELECT * FROM dbo.Orders", None)
        assert any("no program_name" in e.lower() for e in evidence)

    def test_configured_mapping_resolves_unknown(self):
        """An operator can teach SQLPulse about their own applications."""
        workload, evidence = classify_workload(
            "SELECT * FROM dbo.Orders",
            None,
            program_name="MerchPortal.Web",
            application_programs=("MerchPortal",),
        )
        assert workload == WorkloadType.APPLICATION_OR_INTERACTIVE
        assert any("configured application program" in e for e in evidence)

    def test_configured_background_mapping_resolves_unknown(self):
        workload, _ = classify_workload(
            "SELECT * FROM dbo.Orders",
            None,
            program_name="NightlyEtlRunner",
            background_programs=("NightlyEtlRunner",),
        )
        assert workload == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_unknown_workload_does_not_block_slow_classification(self):
        """Not knowing the caller says nothing about whether it was slow."""
        candidates = build_timeout_candidates(
            [_record(9_749.0)],
            threshold_ms=5_000.0,
            database="Merchandising",
        )
        assert candidates[0].workload_type == WorkloadType.UNKNOWN
        assert candidates[0].slow_query_candidate is True


# ─── 5. Broad current-health question considers CPU and I/O ───────────────


class TestBroadCurrentHealthConsidersCpuAndIo:
    def test_prompt_requires_cpu_and_io_for_broad_questions(self):
        assert "get_top_cpu_queries" in SYSTEM_PROMPT
        assert "get_top_io_queries" in SYSTEM_PROMPT
        assert "Steps 5 and 6 are NOT optional" in SYSTEM_PROMPT

    def test_prompt_forbids_deferring_a_callable_tool_to_the_user(self):
        """The agent must not recommend work it can do itself."""
        assert "NEVER end your answer by telling the user to run an approved tool" in (
            SYSTEM_PROMPT
        )

    def test_prompt_warns_that_quiet_is_not_the_same_as_healthy(self):
        assert "is not the same as" in SYSTEM_PROMPT

    def test_tool_budget_allows_the_full_six_step_sweep(self):
        from app.config import get_settings

        assert get_settings().max_agent_tool_calls >= 6, (
            "the prompt asks for six tool calls, so the budget must permit them"
        )

    @pytest.mark.asyncio
    async def test_agent_can_execute_the_full_sweep(self):
        """All six current-performance tools run within one investigation."""
        sweep = [
            "get_database_health",
            "get_active_requests",
            "get_blocking",
            "get_long_running_queries",
            "get_top_cpu_queries",
            "get_top_io_queries",
        ]
        provider = MockLLMProvider([
            _make_tool_call_response(name, {"database": "Merchandising"}, f"tc_{i}")
            for i, name in enumerate(sweep)
        ] + [
            _make_final_response(
                "Summary: Health is fine; two queries are worth tuning.\n"
                "Severity: low\nConfidence: medium\n"
                "Current incident: None observed\n"
                "Optimization opportunities: uspMerch_GetOrders\n"
            ),
        ])

        executors = {name: (lambda _a: {"ok": True}) for name in sweep}
        with patch_executors(executors):
            result = await investigate(
                "What is affecting Merchandising performance right now?", provider,
            )

        for name in sweep:
            assert name in result.tools_used, f"{name} was not called"


# ─── 6. Current incident vs optimization opportunity ──────────────────────


class TestIncidentVersusOptimizationOpportunity:
    def test_prompt_defines_both_and_forbids_merging_them(self):
        assert "CURRENT INCIDENT vs OPTIMIZATION OPPORTUNITY" in SYSTEM_PROMPT
        assert "Do not escalate an optimization opportunity into an incident" in (
            SYSTEM_PROMPT
        )
        assert 'Do not report "no performance concerns"' in SYSTEM_PROMPT

    def test_prompt_explains_why_plan_cache_is_not_live_evidence(self):
        assert "cumulative since the plan was cached" in SYSTEM_PROMPT

    def test_prompt_ties_severity_to_incident_evidence(self):
        assert "Set Severity from the CURRENT INCIDENT evidence" in SYSTEM_PROMPT

    def test_response_format_separates_the_two(self):
        assert "Current incident:" in SYSTEM_PROMPT
        assert "Optimization opportunities:" in SYSTEM_PROMPT

    def test_health_tool_payload_keeps_the_two_separate(self):
        """The evidence handed to the model must already carry the split.

        Without this the model would have to infer an incident from a total,
        which is exactly the mistake being prevented.
        """
        from app.agent.tools import _analysis_to_dict
        from app.detectors.engine import run_analysis
        from app.models.performance import (
            CollectorMetadata,
            PerformanceSnapshot,
            TopCpuQueriesResult,
            TopCpuQuery,
        )

        meta = CollectorMetadata(
            collector_name="test", server_host="srv", database="Merchandising",
        )
        snapshot = PerformanceSnapshot(
            snapshot_id="s1",
            server_host="srv",
            scope="server",
            top_cpu_queries=TopCpuQueriesResult(
                metadata=meta,
                top_n=20,
                queries=[TopCpuQuery(
                    sql_text="SELECT * FROM dbo.Orders",
                    query_hash="0xAAAA",
                    database_name="Merchandising",
                    execution_count=50,
                    total_cpu_time_ms=400_000,
                    avg_cpu_time_ms=8_000.0,
                )],
            ),
        )

        payload = _analysis_to_dict(run_analysis(snapshot, scope="server"))

        # Expensive cached query present, but nothing is happening right now.
        assert payload["optimization_opportunity_count"] >= 1
        assert payload["current_health_status"] == "HEALTHY"
        assert payload["health_finding_count"] == 0

    @pytest.mark.asyncio
    async def test_model_sees_healthy_status_alongside_tuning_candidates(self):
        """A HEALTHY server with expensive queries reports both facts."""
        health_payload = {
            "overall_status": "HEALTHY",
            "current_health_status": "HEALTHY",
            "health_finding_count": 0,
            "optimization_opportunity_count": 2,
            "findings": [],
        }
        provider = MockLLMProvider([
            _make_tool_call_response(
                "get_database_health", {"database": "Merchandising"},
            ),
            _make_final_response(
                "Summary: No current incident; two tuning candidates.\n"
                "Severity: low\nConfidence: medium\n"
                "Current incident: None observed\n"
                "Optimization opportunities: two cached high-CPU queries\n"
                "Human review required: no\n"
            ),
        ])

        with patch_executors({"get_database_health": lambda _a: health_payload}):
            result = await investigate(
                "What is affecting Merchandising performance right now?", provider,
            )

        tool_message = _find_tool_message(provider)
        assert '"current_health_status": "HEALTHY"' in tool_message
        assert '"optimization_opportunity_count": 2' in tool_message
        assert result.severity == "low"


# ─── Helpers ──────────────────────────────────────────────────────────────


def patch_executors(executors: dict):
    """Patch agent tool executors for the duration of a test."""
    from unittest.mock import patch as _patch

    return _patch.dict("app.agent.tools._EXECUTORS", executors)


def _find_tool_message(provider: MockLLMProvider) -> str:
    """Return the tool-result content the model was given."""
    for conversation in reversed(provider.messages_received):
        for message in conversation:
            if message.role == "tool":
                return message.content or ""
    raise AssertionError("no tool result was sent to the model")
