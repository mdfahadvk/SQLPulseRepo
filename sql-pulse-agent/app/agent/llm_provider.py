"""LLM provider abstraction — decouples SQLPulse from any single AI vendor."""

from __future__ import annotations

import json
import logging
from abc import ABC, abstractmethod
from typing import Any

logger = logging.getLogger(__name__)


class LLMError(Exception):
    """Base class for recoverable LLM provider failures."""


class LLMRateLimitError(LLMError):
    """The provider rejected the request because a rate limit was exceeded."""


class LLMPayloadTooLargeError(LLMError):
    """The request exceeded the provider's per-request or per-minute token budget."""


class LLMMessage:
    """A single message in a conversation."""

    __slots__ = ("role", "content", "tool_calls", "tool_call_id", "name")

    def __init__(
        self,
        role: str,
        content: str | None = None,
        tool_calls: list[dict] | None = None,
        tool_call_id: str | None = None,
        name: str | None = None,
    ):
        self.role = role
        self.content = content
        self.tool_calls = tool_calls
        self.tool_call_id = tool_call_id
        self.name = name

    def to_dict(self) -> dict:
        d: dict[str, Any] = {"role": self.role}
        if self.content is not None:
            d["content"] = self.content
        if self.tool_calls is not None:
            d["tool_calls"] = self.tool_calls
        if self.tool_call_id is not None:
            d["tool_call_id"] = self.tool_call_id
        if self.name is not None:
            d["name"] = self.name
        return d


class ToolDefinition:
    """Schema for a tool the LLM can call."""

    __slots__ = ("name", "description", "parameters")

    def __init__(self, name: str, description: str, parameters: dict):
        self.name = name
        self.description = description
        self.parameters = parameters

    def to_openai_schema(self) -> dict:
        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": self.parameters,
            },
        }


class LLMResponse:
    """Normalised response from any LLM provider."""

    __slots__ = ("content", "tool_calls", "finish_reason")

    def __init__(
        self,
        content: str | None = None,
        tool_calls: list[dict] | None = None,
        finish_reason: str = "stop",
    ):
        self.content = content
        self.tool_calls = tool_calls or []
        self.finish_reason = finish_reason

    @property
    def has_tool_calls(self) -> bool:
        return bool(self.tool_calls)


class LLMProvider(ABC):
    """Abstract base class for LLM providers."""

    @abstractmethod
    async def generate(
        self,
        messages: list[LLMMessage],
        tools: list[ToolDefinition] | None = None,
        temperature: float = 0.1,
        max_tokens: int = 4096,
    ) -> LLMResponse:
        """Send messages to the LLM and return a response.

        Args:
            messages: Conversation history.
            tools: Available tool definitions (for function-calling).
            temperature: Sampling temperature.
            max_tokens: Maximum response tokens.

        Returns:
            Normalised LLMResponse.
        """
        ...


class OpenAIProvider(LLMProvider):
    """OpenAI-compatible LLM provider (works with OpenAI, Azure OpenAI, local proxies)."""

    def __init__(
        self,
        api_key: str,
        model: str = "gpt-4o",
        base_url: str | None = None,
        timeout: float = 60.0,
    ):
        if not api_key:
            raise ValueError("LLM_API_KEY is required but not configured")
        try:
            from openai import AsyncOpenAI
        except ImportError:
            raise ImportError(
                "The 'openai' package is required for the AI agent. "
                "Install it with: pip install openai"
            )
        kwargs: dict[str, Any] = {"api_key": api_key, "timeout": timeout, "max_retries": 1}
        if base_url:
            kwargs["base_url"] = base_url
        self._client = AsyncOpenAI(**kwargs)
        self._model = model

    async def generate(
        self,
        messages: list[LLMMessage],
        tools: list[ToolDefinition] | None = None,
        temperature: float = 0.1,
        max_tokens: int = 4096,
    ) -> LLMResponse:
        kwargs: dict[str, Any] = {
            "model": self._model,
            "messages": [m.to_dict() for m in messages],
            "temperature": temperature,
            "max_tokens": max_tokens,
        }
        if tools:
            kwargs["tools"] = [t.to_openai_schema() for t in tools]
            kwargs["tool_choice"] = "auto"

        try:
            response = await self._client.chat.completions.create(**kwargs)
        except Exception as exc:
            raise _translate_provider_error(exc) from exc

        choice = response.choices[0]
        message = choice.message

        tool_calls_out: list[dict] = []
        if message.tool_calls:
            for tc in message.tool_calls:
                tool_calls_out.append({
                    "id": tc.id,
                    "type": "function",
                    "function": {
                        "name": tc.function.name,
                        "arguments": tc.function.arguments,
                    },
                })

        return LLMResponse(
            content=message.content,
            tool_calls=tool_calls_out,
            finish_reason=choice.finish_reason or "stop",
        )


_GROQ_BASE_URL = "https://api.groq.com/openai/v1"


def _translate_provider_error(exc: Exception) -> Exception:
    """Convert a vendor SDK exception into a SQLPulse LLM error where possible.

    Keeps the engine and API layer free of vendor-specific exception types and
    lets them react to the two failures that actually need different handling:
    an oversized request versus a plain rate limit.
    """
    status = getattr(exc, "status_code", None)
    detail = str(exc)

    # Providers report a blown tokens-per-minute budget as either 413 or a 429
    # whose body mentions the token quota. Both are fixed by sending less.
    if status == 413 or "request too large" in detail.lower():
        logger.warning("LLM request rejected as too large: %s", detail)
        return LLMPayloadTooLargeError(detail)

    if status == 429:
        if "tokens per minute" in detail.lower() or "tpm" in detail.lower():
            logger.warning("LLM token-per-minute quota exceeded: %s", detail)
            return LLMPayloadTooLargeError(detail)
        logger.warning("LLM rate limit hit: %s", detail)
        return LLMRateLimitError(detail)

    if status == 400 and "context" in detail.lower() and "length" in detail.lower():
        logger.warning("LLM context length exceeded: %s", detail)
        return LLMPayloadTooLargeError(detail)

    return exc


def create_provider(
    provider_name: str,
    api_key: str,
    model: str = "gpt-4o",
    base_url: str | None = None,
    timeout: float = 60.0,
) -> LLMProvider:
    """Factory function to create an LLM provider by name.

    Supports: 'openai', 'azure_openai', 'groq'.
    """
    name = provider_name.lower().strip()
    if name in ("openai", "azure_openai"):
        return OpenAIProvider(
            api_key=api_key,
            model=model,
            base_url=base_url or None,
            timeout=timeout,
        )
    if name == "groq":
        return OpenAIProvider(
            api_key=api_key,
            model=model,
            base_url=base_url or _GROQ_BASE_URL,
            timeout=timeout,
        )
    raise ValueError(
        f"Unsupported LLM provider: '{provider_name}'. "
        "Supported: openai, azure_openai, groq"
    )
