"""Data sanitization for the AI agent.

Strips credentials, connection strings, API secrets, and unnecessary
SQL parameter values before sending data to the LLM provider.
"""

import json
import re


# Patterns that match sensitive content
_SENSITIVE_PATTERNS = [
    re.compile(r"(?i)(password|pwd)\s*=\s*[^\s;]+"),
    re.compile(r"(?i)(api[_-]?key|secret[_-]?key)\s*=\s*\S+"),
    re.compile(r"(?i)bearer\s+\S+"),
    re.compile(r"(?i)(connection\s*string|conn\s*str)\s*=\s*.+"),
    re.compile(r"(?i)UID=\S+;\s*PWD=\S+"),
    re.compile(r"(?i)SERVER=\S+;.*PWD=[^;]+"),
]

_REDACTED = "[REDACTED]"


def sanitize_text(text: str) -> str:
    """Remove sensitive values from a text string."""
    if not text:
        return text
    result = text
    for pattern in _SENSITIVE_PATTERNS:
        result = pattern.sub(_REDACTED, result)
    return result


def sanitize_dict(data: dict, max_depth: int = 6) -> dict:
    """Recursively sanitize a dictionary, redacting sensitive keys.

    Limits recursion to *max_depth* levels to prevent infinite loops.
    """
    if max_depth <= 0:
        return {"_truncated": True}

    sensitive_keys = frozenset({
        "password", "pwd", "secret", "api_key", "apikey",
        "connection_string", "conn_str", "sql_password",
        "token", "access_token", "refresh_token",
    })

    out: dict = {}
    for key, value in data.items():
        lower_key = key.lower().replace("-", "_")
        if lower_key in sensitive_keys:
            out[key] = _REDACTED
        elif isinstance(value, dict):
            out[key] = sanitize_dict(value, max_depth - 1)
        elif isinstance(value, list):
            out[key] = [
                sanitize_dict(v, max_depth - 1) if isinstance(v, dict) else v
                for v in value
            ]
        elif isinstance(value, str):
            out[key] = sanitize_text(value)
        else:
            out[key] = value
    return out


def sanitize_for_llm(data: dict) -> dict:
    """Prepare tool output for the LLM — strip credentials and trim large payloads."""
    cleaned = sanitize_dict(data)
    return _trim_verbose_fields(cleaned)


# Fields that routinely carry multi-kilobyte SQL/plan text. Only a preview is
# ever useful to the model, and full text quickly exhausts the token budget.
_VERBOSE_TEXT_KEYS = frozenset({
    "sql_text", "sql_text_preview", "query_text", "statement_text",
    "query_sql_text", "command_text", "batch_text", "query_plan",
    "plan_xml", "execution_plan",
})

_VERBOSE_TEXT_LIMIT = 300
_TRUNCATION_SUFFIX = "…[truncated]"


def _trim_verbose_fields(value, max_depth: int = 8):
    """Recursively shorten known-verbose text fields anywhere in the payload."""
    if max_depth <= 0:
        return value

    if isinstance(value, dict):
        out = {}
        for key, val in value.items():
            if (
                key.lower() in _VERBOSE_TEXT_KEYS
                and isinstance(val, str)
                and len(val) > _VERBOSE_TEXT_LIMIT
            ):
                keep = _VERBOSE_TEXT_LIMIT - len(_TRUNCATION_SUFFIX)
                out[key] = val[:keep] + _TRUNCATION_SUFFIX
            else:
                out[key] = _trim_verbose_fields(val, max_depth - 1)
        return out

    if isinstance(value, list):
        return [_trim_verbose_fields(v, max_depth - 1) for v in value]

    return value


def serialize_for_llm(data: dict, max_chars: int) -> str:
    """Serialise tool output to JSON, shrinking it to fit *max_chars*.

    Oversized payloads are reduced by progressively shortening the longest list
    in the result (the usual culprit) before falling back to a hard cut, so the
    model still receives well-formed, representative evidence.
    """
    payload = json.dumps(data, default=str)
    if len(payload) <= max_chars:
        return payload

    if isinstance(data, dict):
        shrunk = dict(data)
        # Repeatedly halve the longest list until the payload fits.
        for _ in range(8):
            list_keys = [
                k for k, v in shrunk.items()
                if isinstance(v, list) and len(v) > 1
            ]
            if not list_keys:
                break
            longest = max(list_keys, key=lambda k: len(shrunk[k]))
            kept = max(1, len(shrunk[longest]) // 2)
            shrunk[longest] = shrunk[longest][:kept]
            shrunk["_truncated"] = True
            shrunk["_truncation_note"] = (
                "Results were truncated to fit the model's token budget. "
                "Counts in this payload reflect the full result set; the item "
                "lists show only the highest-priority entries."
            )
            payload = json.dumps(shrunk, default=str)
            if len(payload) <= max_chars:
                return payload

    return payload[:max_chars - len(_HARD_CUT_SUFFIX)] + _HARD_CUT_SUFFIX


# Closes the JSON object so a hard-cut payload still parses, and flags the cut.
_HARD_CUT_SUFFIX = '..."_truncated":true}'

