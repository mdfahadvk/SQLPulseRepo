namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// A recoverable LLM provider failure.
/// Ported from <c>LLMError</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
/// <remarks>
/// Vendor exception types are translated into this hierarchy at the HTTP boundary so the
/// rest of SQLPulse reacts to the failures that actually need different handling, without
/// depending on any single provider's SDK.
/// </remarks>
public class LlmException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The provider rejected the request because a rate limit was exceeded (HTTP 429).
/// Python: <c>LLMRateLimitError</c>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="LlmPayloadTooLargeException"/> because the fix is different:
/// a plain rate limit is fixed by waiting, an oversized request by sending less.
/// </remarks>
public sealed class LlmRateLimitException(string message, TimeSpan? retryAfter = null, Exception? innerException = null)
    : LlmException(message, innerException)
{
    /// <summary>Provider-supplied <c>Retry-After</c> hint, when one was sent.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The request exceeded the provider's per-request or per-minute token budget.
/// Python: <c>LLMPayloadTooLargeError</c>.
/// </summary>
/// <remarks>
/// Raised for HTTP 413, for a 429 whose body names the token-per-minute quota, and for a
/// 400 reporting an exceeded context length. All three are fixed by sending less, so the
/// caller can compact the conversation and retry rather than giving up.
/// </remarks>
public sealed class LlmPayloadTooLargeException(string message, Exception? innerException = null)
    : LlmException(message, innerException);

/// <summary>The provider could not be reached, or returned a server-side error.</summary>
public sealed class LlmUnavailableException(string message, Exception? innerException = null)
    : LlmException(message, innerException);

/// <summary>The provider did not respond within the configured request timeout.</summary>
public sealed class LlmTimeoutException(string message, Exception? innerException = null)
    : LlmException(message, innerException);

/// <summary>The provider rejected the configured credentials.</summary>
public sealed class LlmAuthenticationException(string message, Exception? innerException = null)
    : LlmException(message, innerException);

/// <summary>The provider rejected the request as invalid.</summary>
public sealed class LlmInvalidRequestException(string message, Exception? innerException = null)
    : LlmException(message, innerException);

/// <summary>
/// No LLM provider or API key is configured.
/// </summary>
/// <remarks>
/// Reported as its own failure so the caller can say "AI features are not configured"
/// instead of surfacing a provider authentication error, which would suggest a wrong key
/// rather than a missing one.
/// </remarks>
public sealed class LlmNotConfiguredException(string message)
    : LlmException(message);
