using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// LLM provider settings (Python: <c>LLM_*</c>). The API key must be supplied via
/// environment variables or user secrets and is never logged.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Provider identifier, for example <c>openai</c>.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>API key. Supply via environment variable or user secrets — never commit.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model name, for example <c>gpt-4o</c>.</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>Optional base URL for OpenAI-compatible endpoints.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Sampling temperature. Kept low for deterministic analysis.</summary>
    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.1;

    /// <summary>HTTP request timeout in seconds for a single LLM call.</summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum tokens requested per completion.</summary>
    [Range(1, 200_000)]
    public int MaxTokens { get; set; } = 1_500;

    /// <summary>Cooldown applied after all rate-limit retries are exhausted.</summary>
    [Range(1, 3_600)]
    public int RateLimitCooldownSeconds { get; set; } = 30;

    /// <summary>True when a provider and API key are both configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(ApiKey);
}
