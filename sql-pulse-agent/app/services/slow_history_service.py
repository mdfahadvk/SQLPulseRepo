"""Shared slow-history application service."""

from __future__ import annotations

import logging
from datetime import datetime

from app.collectors.query_store import collect_slow_history
from app.config import get_settings
from app.detectors.timeout_candidate import build_timeout_candidates
from app.models.slow_history import SlowHistoryResponse, WorkloadType

logger = logging.getLogger(__name__)


def get_slow_history_response(
    *,
    database: str,
    start_time: datetime,
    end_time: datetime,
    threshold_ms: int,
    top_n: int = 20,
    group_results: bool = True,
    workload_type: WorkloadType | None = None,
    application_timeout_ms: int | None = None,
) -> SlowHistoryResponse:
    """Return the same slow-history response used by API and AI tool callers.

    ``threshold_ms`` selects which queries are slow enough to report.  It is
    never reused as the application timeout: when ``application_timeout_ms`` is
    not supplied here it falls back to the configured
    ``settings.application_timeout_ms``, which is None unless an operator has
    stated the application's real timeout.
    """
    settings = get_settings()
    threshold = float(threshold_ms)
    app_timeout = (
        application_timeout_ms
        if application_timeout_ms is not None
        else settings.application_timeout_ms
    )

    qs_status, slow_records, qs_message = collect_slow_history(
        database=database,
        start_time=start_time,
        end_time=end_time,
        threshold_ms=threshold,
        top_n=top_n,
    )

    if not qs_status.is_accessible:
        logger.info(
            "slow_history candidates: database=%s start_time=%s end_time=%s "
            "threshold_ms=%d top_n=%d workload_type=%s query_store_status=%s "
            "candidate_count=%d",
            database,
            start_time.isoformat(),
            end_time.isoformat(),
            threshold_ms,
            top_n,
            workload_type.value if workload_type else "ALL",
            qs_status.value,
            0,
        )
        return SlowHistoryResponse(
            server=settings.sql_server,
            database=database,
            start_time=start_time,
            end_time=end_time,
            threshold_ms=threshold_ms,
            slow_threshold_ms=threshold_ms,
            application_timeout_ms=app_timeout,
            timeout_classification_available=app_timeout is not None,
            query_store_status=qs_status,
            top_n=top_n,
            message=qs_message,
        )

    candidates = build_timeout_candidates(
        slow_records,
        threshold,
        database,
        group_results=group_results,
        workload_type_filter=workload_type,
        application_timeout_ms=float(app_timeout) if app_timeout is not None else None,
    )

    logger.info(
        "slow_history candidates: database=%s start_time=%s end_time=%s "
        "threshold_ms=%d application_timeout_ms=%s top_n=%d workload_type=%s "
        "query_store_status=%s candidate_count=%d timeout_candidate_count=%d",
        database,
        start_time.isoformat(),
        end_time.isoformat(),
        threshold_ms,
        app_timeout if app_timeout is not None else "not_configured",
        top_n,
        workload_type.value if workload_type else "ALL",
        qs_status.value,
        len(candidates),
        sum(1 for c in candidates if c.timeout_candidate),
    )

    return SlowHistoryResponse(
        server=settings.sql_server,
        database=database,
        start_time=start_time,
        end_time=end_time,
        threshold_ms=threshold_ms,
        slow_threshold_ms=threshold_ms,
        application_timeout_ms=app_timeout,
        timeout_classification_available=app_timeout is not None,
        query_store_status=qs_status,
        top_n=top_n,
        total_candidates=len(candidates),
        slow_query_candidate_count=sum(
            1 for c in candidates if c.slow_query_candidate
        ),
        timeout_candidate_count=sum(1 for c in candidates if c.timeout_candidate),
        candidates=candidates,
    )
