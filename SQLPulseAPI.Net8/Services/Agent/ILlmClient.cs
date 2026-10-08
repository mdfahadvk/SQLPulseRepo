namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// Abstraction over the LLM provider used by the investigation agent.
/// Ported from <c>LLMProvider</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
/// <remarks>
/// This is the seam that keeps SQLPulse independent of any single AI vendor, and the seam
/// that lets the agent be tested without ever calling a real provider.
/// </remarks>
public interface ILlmClient
{
    /// <summary>True when a provider and API key are both configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a conversation to the model and returns a normalised response.
    /// </summary>
    /// <param name="messages">Conversation history.</param>
    /// <param name="tools">
    /// Tools the model may call. Pass <see langword="null"/> to request a plain completion;
    /// providers reject a history containing tool calls when tools are absent, so the
    /// caller must flatten the conversation first.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="LlmNotConfiguredException">No provider or API key is configured.</exception>
    /// <exception cref="LlmRateLimitException">The provider returned HTTP 429.</exception>
    /// <exception cref="LlmPayloadTooLargeException">The request exceeded the provider's token budget.</exception>
    /// <exception cref="LlmTimeoutException">The provider did not respond in time.</exception>
    /// <exception cref="LlmUnavailableException">The provider could not be reached.</exception>
    Task<LlmCompletion> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
