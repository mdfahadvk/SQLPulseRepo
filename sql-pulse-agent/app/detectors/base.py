"""Base class for all performance detectors."""

import uuid
from abc import ABC, abstractmethod
from datetime import UTC, datetime

from app.detectors.query_filters import classify_exclusion
from app.detectors.thresholds import DetectorThresholds, get_thresholds
from app.models.findings import Confidence, ExcludedQuery, PerformanceFinding
from app.models.performance import PerformanceSnapshot

# Ordered weakest-first so confidence can be stepped down by index.
_CONFIDENCE_LADDER = (Confidence.LOW, Confidence.MEDIUM, Confidence.HIGH)


class BaseDetector(ABC):
    """Abstract base for deterministic performance detectors."""

    def __init__(self, thresholds: DetectorThresholds | None = None) -> None:
        self.thresholds = thresholds or get_thresholds()
        # Queries this detector deliberately skipped. Read by the engine after
        # detect() so exclusions can be surfaced for debugging.
        self.excluded_queries: list[ExcludedQuery] = []

    @property
    @abstractmethod
    def name(self) -> str:
        """Detector name."""

    def _generate_finding_id(self) -> str:
        return str(uuid.uuid4())

    def _now(self) -> datetime:
        return datetime.now(UTC)

    def _is_monitoring_query(self, query) -> bool:
        """True if *query* is diagnostic noise; records the reason when so.

        Monitoring queries are recorded rather than silently dropped, so an
        operator can confirm the filter is not hiding real workload.
        """
        reason = classify_exclusion(getattr(query, "sql_text", None))
        if reason is None:
            return False
        self.excluded_queries.append(ExcludedQuery(
            reason=reason,
            query_hash=getattr(query, "query_hash", None),
            database=getattr(query, "database_name", None),
            sql_preview=(getattr(query, "sql_text", None) or "")[:150] or None,
        ))
        return True

    @staticmethod
    def _weaken_confidence(confidence: Confidence, steps: int = 1) -> Confidence:
        """Step *confidence* down, never below LOW."""
        index = _CONFIDENCE_LADDER.index(confidence)
        return _CONFIDENCE_LADDER[max(0, index - steps)]

    @abstractmethod
    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        """Analyze snapshot data and return findings. Must be deterministic."""
