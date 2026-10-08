"""Detector for blocking sessions and blocking chains."""

import logging

from app.detectors.base import BaseDetector
from app.models.findings import (
    Confidence,
    FindingCategory,
    PerformanceFinding,
    Severity,
)
from app.models.performance import PerformanceSnapshot

logger = logging.getLogger(__name__)


class BlockingDetector(BaseDetector):
    """Detects significant blocking based on wait time and chain depth."""

    @property
    def name(self) -> str:
        return "blocking_detector"

    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        findings: list[PerformanceFinding] = []

        if not snapshot.blocking_sessions or not snapshot.blocking_sessions.metadata.success:
            return findings

        sessions = snapshot.blocking_sessions.sessions
        if not sessions:
            return findings

        t = self.thresholds
        now = self._now()

        # Group by head blocker
        head_blockers = [s for s in sessions if s.is_head_blocker]
        blocked_sessions = [s for s in sessions if not s.is_head_blocker]

        # Find the maximum wait time across all blocked sessions
        max_wait_ms = max((s.wait_time_ms for s in sessions), default=0)
        total_blocked = len(sessions)

        # Determine severity based on wait time and chain size
        if (
            max_wait_ms >= t.blocking_critical_wait_ms
            or total_blocked >= t.blocking_critical_chain_count
        ):
            severity = Severity.CRITICAL
            confidence = Confidence.HIGH
        elif max_wait_ms >= t.blocking_high_wait_ms:
            severity = Severity.HIGH
            confidence = Confidence.HIGH
        elif max_wait_ms >= t.blocking_warning_wait_ms:
            severity = Severity.MEDIUM
            confidence = Confidence.HIGH
        else:
            # Below all thresholds — no finding
            return findings

        evidence = []
        for s in sessions:
            blocker_info = (
                f"blocked by session {s.blocking_session_id}"
                if s.blocking_session_id
                else "HEAD BLOCKER"
            )
            text_preview = (s.sql_text or "N/A")[:100]
            evidence.append(
                f"Session {s.session_id} ({blocker_info}): "
                f"wait={s.wait_time_ms}ms, type={s.wait_type or 'N/A'}, "
                f"resource={s.wait_resource or 'N/A'} — {text_preview}"
            )

        summary = (
            f"Blocking chain detected: {len(head_blockers)} head blocker(s), "
            f"{total_blocked} total session(s) involved, "
            f"max wait time {max_wait_ms / 1000:.1f}s"
        )

        findings.append(PerformanceFinding(
            finding_id=self._generate_finding_id(),
            category=FindingCategory.BLOCKING,
            server=snapshot.server_host,
            database=snapshot.database,
            severity=severity,
            confidence=confidence,
            first_observed=now,
            last_observed=now,
            summary=summary,
            evidence=evidence,
            likely_cause=(
                "A session is holding locks that other sessions need. "
                "The specific root cause (long transaction, application logic, "
                "lock escalation) requires examining the head blocker's session state."
            ),
            recommendation=(
                "Identify the head blocker's query and determine why it holds locks. "
                "Check for uncommitted transactions via DBCC OPENTRAN, "
                "then examine the blocker's execution plan and session state."
            ),
            validation_steps=[
                "Identify the head blocker session and its current query",
                "Check if the head blocker has open uncommitted transactions",
                "Review the lock resource to determine the contention point",
                "Check if the blocking query can be optimized or split",
            ],
            human_review_required=(severity in (Severity.CRITICAL, Severity.HIGH)),
        ))

        return findings
