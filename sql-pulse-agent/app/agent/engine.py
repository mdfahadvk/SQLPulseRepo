"""AI investigation engine — tool-calling agent loop.

Follows the UNDERSTAND → OBSERVE → ASSESS → CORRELATE → PRIORITIZE → RECOMMEND
flow.  The agent chooses which SQLPulse tools to call.  It never generates SQL
and never modifies the monitored server.

Guard-rails:
  * Maximum tool calls (MAX_AGENT_TOOL_CALLS)
  * Maximum wall-clock time (MAX_AGENT_EXECUTION_SECONDS)
  * Only approved tools may be invoked
  * All tool output is sanitised before reaching the LLM
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import time
import uuid
from datetime import datetime
from typing import Any

from app.agent.llm_provider import (
    LLMMessage,
    LLMPayloadTooLargeError,
    LLMProvider,
    LLMResponse,
    ToolDefinition,
)
from app.agent.tools import APPROVED_TOOL_NAMES, TOOL_DEFINITIONS, execute_tool
from app.agent.sanitizer import sanitize_dict, serialize_for_llm
from app.config import get_settings
from app.models.agent import InvestigationResponse, ToolCall

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = """\
You are SQLPulse AI — a read-only SQL Server performance investigation agent.

ROLE:
You observe, explain, correlate, and recommend.  You NEVER modify the database.

─── INVESTIGATION STRATEGY ───

First classify the user's question:

CURRENT PERFORMANCE QUESTION (e.g. "Why is Merchandising slow right now?",
"What is affecting Merchandising performance right now?"):
  For a BROAD current-performance question, normally gather all six before
  concluding anything — including concluding that nothing is wrong:
    1. get_database_health (or get_server_health if no database specified)
    2. get_active_requests (scoped to the database)
    3. get_blocking
    4. get_long_running_queries
    5. get_top_cpu_queries
    6. get_top_io_queries
  Steps 5 and 6 are NOT optional for a broad question.  "Nothing is blocking
  and nothing is running long" is not the same as "there is nothing worth
  reporting", and you cannot tell the difference without looking at CPU and
  I/O.  You may stop early only for a NARROW question that names its own
  subject (e.g. "is anything blocking right now?").
  Do NOT call get_slow_history or get_timeout_candidates unless the evidence
  from current tools suggests a recurring historical pattern.

HISTORICAL PERFORMANCE QUESTION (e.g. "What was slow in Merchandising yesterday?"):
  Preferred tool path:
    1. get_slow_history (with the relevant time range)
    2. get_timeout_candidates (same time range)
    3. get_query_baseline (for the strongest candidate's query_hash)
    4. get_query_history (only if baseline shows regression)

TIMEOUT / SLOW QUERY QUESTION (e.g. "Which query caused the Merchandising timeout?"):
  Preferred tool path:
    1. get_timeout_candidates (database, time range, threshold)
    2. get_slow_history (same parameters for supporting context)
    3. get_query_baseline (for the top candidate)
    4. get_active_requests or get_blocking ONLY if the timeframe is current
  Prefer APPLICATION_OR_INTERACTIVE candidates when the question specifically
  concerns an application timeout.  Mention BACKGROUND_OR_SCHEDULED workloads
  only if they could have contributed to server pressure.

REGRESSION QUESTION (e.g. "Has anything regressed in Sales?"):
  Preferred tool path:
    1. get_query_regressions (database)
    2. get_query_baseline (for the top regressed query)
    3. get_query_history (to show the trend)

SERVER-WIDE QUESTION (e.g. "How is the SQL Server doing?"):
  Preferred tool path:
    1. get_server_health
    2. get_active_requests (no database filter)
    3. Drill into a specific database ONLY if findings identify one

─── TOOL USAGE RULES ───

- Use the SMALLEST tool path that can actually answer the question asked.
  A narrow question deserves a narrow path; a broad "what is affecting X"
  question needs the full current-performance sweep above before you can
  claim to have answered it.
- Continue drilling down ONLY when returned evidence justifies it.
- NEVER end your answer by telling the user to run an approved tool yourself
  could have called.  If a tool in your list would answer the open question
  and you have budget remaining, CALL IT.  "Recommended investigation" is for
  work outside your reach — application telemetry, execution plan review,
  code or index changes, business context — not for get_top_cpu_queries.
- Respect the existing tool-call and execution-time limits.  You have enough
  budget for the six-step current-performance path.
- Only use the tools provided.  Do NOT generate SQL.

─── CURRENT INCIDENT vs OPTIMIZATION OPPORTUNITY ───

These are different claims and must never be merged.

CURRENT INCIDENT — something is going wrong right now.
  Requires live evidence: active blocking chains, queries running long right
  now, an unhealthy status from a health tool.

OPTIMIZATION OPPORTUNITY — something is expensive, but not an active problem.
  Plan-cache statistics (get_top_cpu_queries / get_top_io_queries) are
  cumulative since the plan was cached.  A query at the top of those lists
  proves it has been expensive over time; it does NOT prove anything is wrong
  at this moment.

So a healthy database with expensive cached queries is the normal case, and
the honest answer is BOTH facts:
  "No current incident: no blocking, no long-running queries, health is
   HEALTHY.  Optimization opportunities exist: <query> has consumed <X> CPU
   across <N> executions."

Do not escalate an optimization opportunity into an incident.
Do not report "no performance concerns" when CPU/I/O evidence shows
expensive queries — say the health is fine AND name what is worth tuning.
Set Severity from the CURRENT INCIDENT evidence, not from cached cost.

─── EVIDENCE RULES ───

Every technical statement must originate from tool evidence.

Never invent:
  - timeout events, blocking sessions, execution plan issues
  - index recommendations, procedure names, query hashes
  - durations, CPU times, logical reads, regressions

Explicitly distinguish between:
  CONFIRMED FACT — directly observed in tool output
  SUPPORTED INFERENCE — reasonable conclusion from multiple observations
  UNKNOWN / REQUIRES MORE EVIDENCE — state clearly what you cannot determine

─── SLOW QUERY vs TIMEOUT LANGUAGE ───

Three distinct claims, in increasing strength:

SLOW_QUERY_CANDIDATE
  Exceeded the slow-query monitoring threshold.  The threshold is chosen by
  whoever configured monitoring and is arbitrary.  Means "worth attention".

TIMEOUT_CANDIDATE
  Also reached an EXPLICITLY CONFIGURED application timeout, so the client
  could plausibly have given up.

CONFIRMED APPLICATION TIMEOUT
  Application telemetry recorded an actual timeout exception.

Rules:
- Read each candidate's "classification" field.  Do not derive it yourself.
- A query exceeding a 5-second monitoring threshold is NOT a timeout
  candidate.  Exceeding a monitoring threshold says nothing about when the
  application gives up.
- If "timeout_classification_available" is false, no application timeout is
  configured.  Say the timeout question cannot be answered and recommend
  configuring the application timeout.  Do NOT substitute the monitoring
  threshold for it.
- SQLPulse has no application telemetry, so NEVER say
  "CONFIRMED APPLICATION TIMEOUT".

─── WORKLOAD CLASSIFICATION ───

Report the workload_type the tool returned, including UNKNOWN.

UNKNOWN means the available evidence was too weak to decide — most often
because Query Store does not record program_name.  It is a real, correct
answer.  Do not upgrade it by reasoning from a procedure's name: "usp"
prefixes are a naming convention, and application and background code are
named the same way.  Quote classification_evidence when the type matters.

─── RESPONSE FORMAT ───

Summary: <one-paragraph summary>
Severity: <critical / high / medium / low / unknown>
Confidence: <high / medium / low>
Current incident: <what is wrong right now, or "None observed" with the live
  evidence that rules it out>
Optimization opportunities: <expensive queries worth tuning, or "None observed">
Affected database: <name or N/A>
Affected query/procedure: <name or N/A>
Evidence:
- <bullet list of observed facts from tool output>
Historical comparison: <baseline comparison or N/A>
Likely cause: <your assessment based ONLY on evidence>
What is known:
- <bullet list of confirmed facts>
What is uncertain:
- <bullet list of unknowns>
Recommended investigation:
- <bullet list of next steps>
Human review required: <yes / no>
Agent action: No database remediation was performed. Observation and recommendation only.

─── DATE/TIME CONTEXT ───

Current application date/time: {current_datetime}

When the user mentions a date without a year (e.g. "August 21"), resolve it to
the most recent past occurrence relative to the current date above.
Always use ISO 8601 format for tool arguments: YYYY-MM-DDTHH:MM:SS.

For full-day queries, use a half-open interval:
  start_time = YYYY-MM-DDT00:00:00
  end_time   = (next day) YYYY-MM-DDT00:00:00

Do NOT use 23:59:59 as end_time.
Do NOT guess or fabricate years — use the current date context above.
"""


async def investigate(
    question: str,
    provider: LLMProvider,
) -> InvestigationResponse:
    """Run a full AI investigation for a user question.

    Returns a structured InvestigationResponse with evidence-backed findings.
    """
    settings = get_settings()
    investigation_id = str(uuid.uuid4())
    max_calls = settings.max_agent_tool_calls
    max_seconds = settings.max_agent_execution_seconds
    start_time = time.monotonic()

    logger.info(
        "Investigation %s started — question: %s",
        investigation_id,
        question[:200],
    )

    messages: list[LLMMessage] = [
        LLMMessage(
            role="system",
            content=SYSTEM_PROMPT.format(
                current_datetime=datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
            ),
        ),
        LLMMessage(role="user", content=question),
    ]

    tool_calls_log: list[ToolCall] = []
    tools_used_names: list[str] = []
    total_calls = 0

    async def generate(tools: list[ToolDefinition] | None) -> LLMResponse:
        """Call the LLM, shrinking the conversation if the provider rejects its size.

        Summarisation calls pass no tools, so the conversation is flattened
        first: providers reject a history containing tool calls when tools are
        absent ("tool choice is none, but model called a tool").
        """
        outbound = messages if tools else _flatten_for_summary(messages)
        try:
            return await provider.generate(
                outbound,
                tools=tools,
                temperature=settings.llm_temperature,
                max_tokens=settings.llm_max_tokens,
            )
        except LLMPayloadTooLargeError:
            logger.warning(
                "Investigation %s exceeded the provider token budget — "
                "compacting evidence and retrying",
                investigation_id,
            )
            _compact_tool_messages(messages)
            # The retry drops the tool schemas too: at this point the goal is a
            # final answer from the evidence already collected, not more calls.
            return await provider.generate(
                _flatten_for_summary(messages),
                tools=None,
                temperature=settings.llm_temperature,
                max_tokens=settings.llm_max_tokens,
            )

    # Agent loop — let the LLM decide which tools to call
    while True:
        elapsed = time.monotonic() - start_time
        if elapsed > max_seconds:
            logger.warning(
                "Investigation %s hit time limit (%ds)",
                investigation_id, max_seconds,
            )
            messages.append(LLMMessage(
                role="user",
                content=(
                    "Time limit reached. Please provide your analysis "
                    "based on the evidence gathered so far."
                ),
            ))
            # One final generation without tools
            response = await generate(tools=None)
            break

        if total_calls >= max_calls:
            logger.warning(
                "Investigation %s hit tool-call limit (%d)",
                investigation_id, max_calls,
            )
            messages.append(LLMMessage(
                role="user",
                content=(
                    "Tool-call limit reached. Please provide your analysis "
                    "based on the evidence gathered so far."
                ),
            ))
            response = await generate(tools=None)
            break

        response = await generate(tools=TOOL_DEFINITIONS)

        if not response.has_tool_calls:
            break

        # Process tool calls
        # Append assistant message with tool_calls
        messages.append(LLMMessage(
            role="assistant",
            content=response.content,
            tool_calls=response.tool_calls,
        ))

        for tc in response.tool_calls:
            tc_id = tc.get("id", "")
            func = tc.get("function", {})
            tool_name = func.get("name", "")
            raw_args = func.get("arguments", "{}")

            try:
                args = json.loads(raw_args) if isinstance(raw_args, str) else raw_args
            except json.JSONDecodeError:
                args = {}

            total_calls += 1
            call_start = time.monotonic()

            # Safety: only approved tools
            if tool_name not in APPROVED_TOOL_NAMES:
                result_data = {"error": f"Tool '{tool_name}' is not approved"}
                tool_calls_log.append(ToolCall(
                    tool_name=tool_name, arguments=args,
                    arguments_sanitized=sanitize_dict(args),
                    result_summary={"error": "Not an approved tool"},
                    success=False, error="Not an approved tool",
                    duration_seconds=0.0,
                ))
            else:
                # Tools issue blocking ODBC queries — keep them off the event loop.
                result_data = await asyncio.to_thread(execute_tool, tool_name, args)
                call_duration = time.monotonic() - call_start
                success = "error" not in result_data
                tool_calls_log.append(ToolCall(
                    tool_name=tool_name,
                    arguments=args,
                    arguments_sanitized=sanitize_dict(args),
                    result_summary=_build_result_summary(tool_name, result_data),
                    success=success,
                    error=result_data.get("error"),
                    duration_seconds=round(call_duration, 2),
                ))
                if tool_name not in tools_used_names:
                    tools_used_names.append(tool_name)

                logger.info(
                    "Investigation %s — tool %s completed (success=%s, %.1fs)",
                    investigation_id, tool_name, success, call_duration,
                )

            # Feed result back to the LLM, bounded so a single large snapshot
            # cannot exhaust the model context or the provider's token quota.
            messages.append(LLMMessage(
                role="tool",
                content=serialize_for_llm(result_data, settings.max_tool_result_chars),
                tool_call_id=tc_id,
                name=tool_name,
            ))

    duration = round(time.monotonic() - start_time, 2)
    answer_text = response.content or "Unable to produce an analysis."

    # Parse structured fields from the answer
    parsed = _parse_structured_answer(answer_text)

    result = InvestigationResponse(
        investigation_id=investigation_id,
        question=question,
        answer=answer_text,
        severity=parsed.get("severity", "unknown"),
        confidence=parsed.get("confidence", "low"),
        affected_database=parsed.get("affected_database"),
        affected_query=parsed.get("affected_query"),
        evidence=parsed.get("evidence", []),
        historical_comparison=parsed.get("historical_comparison"),
        likely_cause=parsed.get("likely_cause"),
        what_is_known=parsed.get("what_is_known", []),
        what_is_uncertain=parsed.get("what_is_uncertain", []),
        recommended_investigation=parsed.get("recommended_investigation", []),
        human_review_required=parsed.get("human_review_required", True),
        agent_action="No database remediation was performed. Observation and recommendation only.",
        tools_used=tools_used_names,
        tool_calls_detail=tool_calls_log,
        tool_call_count=total_calls,
        duration_seconds=duration,
    )

    logger.info(
        "Investigation %s completed — severity=%s confidence=%s tools=%d duration=%.1fs",
        investigation_id,
        result.severity,
        result.confidence,
        total_calls,
        duration,
    )

    return result


def _flatten_for_summary(messages: list[LLMMessage]) -> list[LLMMessage]:
    """Rebuild the conversation as plain text, without tool-call plumbing.

    A summarisation call offers no tools, and providers reject a history that
    still contains tool calls in that case. Tool results are preserved as
    ordinary evidence text so the model keeps everything it needs to answer.
    """
    flat: list[LLMMessage] = []
    for msg in messages:
        if msg.role == "tool":
            flat.append(LLMMessage(
                role="user",
                content=f"Evidence from {msg.name or 'tool'}:\n{msg.content or ''}",
            ))
        elif msg.role == "assistant" and msg.tool_calls:
            # The request to call a tool is noise once the results are inlined.
            if msg.content:
                flat.append(LLMMessage(role="assistant", content=msg.content))
        else:
            flat.append(msg)
    return flat


def _compact_tool_messages(messages: list[LLMMessage]) -> None:
    """Shrink accumulated tool output in place so the conversation fits again.

    Keeps the most recent tool result at a reduced size and replaces older ones
    with a short placeholder — recent evidence is what the model still needs to
    reason about, while older results have already shaped the conversation.
    """
    tool_indexes = [i for i, m in enumerate(messages) if m.role == "tool"]
    if not tool_indexes:
        return

    newest = tool_indexes[-1]
    for i in tool_indexes:
        content = messages[i].content or ""
        if i == newest:
            limit = _COMPACTED_RECENT_CHARS
        else:
            limit = _COMPACTED_OLDER_CHARS
        if len(content) > limit:
            messages[i].content = content[:limit] + " …[evidence truncated to fit token budget]"


_COMPACTED_RECENT_CHARS = 2000
_COMPACTED_OLDER_CHARS = 400


def _build_result_summary(tool_name: str, result_data: dict) -> dict:
    """Extract a concise, sanitized summary from tool output for audit logging."""
    summary: dict[str, Any] = {}
    if "error" in result_data:
        summary["error"] = result_data["error"]
        return summary

    # Copy scalar metadata keys
    for key in (
        "database", "query_store_status", "record_count",
        "candidate_count", "overall_status", "total_findings",
        "regression_count",
    ):
        if key in result_data:
            summary[key] = result_data[key]

    # For list results, include count but not full payloads
    for key in ("records", "candidates", "findings", "regressions"):
        if key in result_data and isinstance(result_data[key], list):
            summary[f"{key}_count"] = len(result_data[key])

    return summary


def _parse_structured_answer(text: str) -> dict[str, Any]:
    """Best-effort extraction of structured fields from the LLM's free-text answer."""
    result: dict[str, Any] = {}

    lines = text.split("\n")

    for line in lines:
        # Strip markdown bold/italic markers and leading whitespace
        cleaned = re.sub(r"\*{1,2}", "", line).strip()
        lower = cleaned.lower()

        # Extract value after the colon and strip parenthetical qualifiers
        def _val_after_colon(s: str) -> str:
            raw = s.split(":", 1)[1].strip() if ":" in s else ""
            # Remove parenthetical like "(no performance issue detected)"
            raw = re.sub(r"\s*\(.*?\)\s*$", "", raw).strip()
            # Remove trailing markdown or punctuation
            return raw.rstrip("*").strip()

        if lower.startswith("severity:"):
            val = _val_after_colon(cleaned).lower()
            if val in ("critical", "high", "medium", "low", "unknown"):
                result["severity"] = val

        elif lower.startswith("confidence:"):
            val = _val_after_colon(cleaned).lower()
            if val in ("high", "medium", "low"):
                result["confidence"] = val

        elif lower.startswith("affected database:"):
            val = _val_after_colon(cleaned)
            if val and val.lower() not in ("n/a", "none"):
                result["affected_database"] = val

        elif lower.startswith("affected query") or lower.startswith("affected procedure"):
            val = _val_after_colon(cleaned) if ":" in cleaned else ""
            if val and val.lower() not in ("n/a", "none"):
                result["affected_query"] = val

        elif lower.startswith("historical comparison:"):
            val = _val_after_colon(cleaned)
            if val and val.lower() not in ("n/a", "none"):
                result["historical_comparison"] = val

        elif lower.startswith("likely cause:"):
            val = _val_after_colon(cleaned)
            if val:
                result["likely_cause"] = val

        elif lower.startswith("human review required:"):
            val = _val_after_colon(cleaned).lower()
            result["human_review_required"] = val != "no"

    # Extract bullet lists
    result["evidence"] = _extract_bullet_section(text, "evidence:")
    result["what_is_known"] = _extract_bullet_section(text, "what is known:")
    result["what_is_uncertain"] = _extract_bullet_section(text, "what is uncertain:")
    result["recommended_investigation"] = _extract_bullet_section(
        text, "recommended investigation:"
    )

    return result


def _extract_bullet_section(text: str, header: str) -> list[str]:
    """Extract bullet points following a section header.

    Handles markdown-formatted headers like **Evidence** or **Evidence:**.
    """
    # Strip markdown bold markers for matching
    plain = re.sub(r"\*{1,2}", "", text)
    lower = plain.lower()
    idx = lower.find(header)
    if idx == -1:
        return []

    after = plain[idx + len(header):]
    items: list[str] = []
    for line in after.split("\n"):
        stripped = line.strip()
        if stripped.startswith("- ") or stripped.startswith("* "):
            items.append(stripped[2:].strip())
        elif items and not stripped:
            # Empty line after bullets — end of section
            break
        elif items and not stripped.startswith(("-", "*")) and ":" in stripped:
            # Hit the next section header
            break
    return items
