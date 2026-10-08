using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// Talks to any OpenAI-compatible chat-completions endpoint, including Groq.
/// Ported from <c>OpenAIProvider</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// OpenAI, Azure OpenAI and Groq all speak the same <c>/chat/completions</c> protocol, so
/// one client covers every supported provider and swapping between them is a configuration
/// change rather than a code change. Talking to the HTTP API directly, instead of a vendor
/// SDK, keeps the dependency surface small and the failure modes explicit.
/// </para>
/// <para>
/// The API key is attached as a request header and never written to a log, an exception
/// message or the payload sent to the model.
/// </para>
/// </remarks>
public sealed partial class OpenAiCompatibleLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> options,
    ILogger<OpenAiCompatibleLlmClient> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? utcNow = null) : ILlmClient
{
    /// <summary>Named <see cref="HttpClient"/> registered for LLM traffic.</summary>
    public const string HttpClientName = "SQLPulse.Llm";

    /// <summary>Groq's OpenAI-compatible endpoint. Python: <c>_GROQ_BASE_URL</c>.</summary>
    public const string GroqBaseUrl = "https://api.groq.com/openai/v1";

    /// <summary>Default endpoint when no base URL is configured.</summary>
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    private const string CompletionsPath = "chat/completions";

    /// <summary>Caps how much provider error text is copied into an exception or log entry.</summary>
    private const int MaxErrorDetailChars = 500;
    private static readonly TimeSpan[] RateLimitRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];
    private static readonly TimeSpan MaxRateLimitDelay = TimeSpan.FromSeconds(30);

    private readonly LlmOptions _options = options.Value;
    private readonly object _cooldownGate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? _cooldownUntil;

    /// <inheritdoc />
    public bool IsConfigured => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<LlmCompletion> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!IsConfigured)
        {
            throw new LlmNotConfiguredException(
                "An LLM API key is required but is not configured.");
        }

        if (GetCooldownRemaining() is { } cooldown)
        {
            throw new LlmRateLimitException(
                "AI provider is temporarily rate-limited. Please try again shortly.",
                cooldown);
        }

        var baseUrl = ResolveBaseUrl(_options.Provider, _options.BaseUrl);
        var body = BuildRequestBody(messages, tools);
        var client = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; ; attempt++)
        {
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{CompletionsPath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            try
            {
                using var response = await client.SendAsync(request, linkedSource.Token).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(linkedSource.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return ParseCompletion(payload);
                }

                var translated = TranslateError(response, payload, _options.Provider, logger);
                if (translated is LlmRateLimitException rateLimit
                    && attempt <= RateLimitRetryDelays.Length)
                {
                    var wait = (rateLimit.RetryAfter ?? RateLimitRetryDelays[attempt - 1]);
                    wait = wait > MaxRateLimitDelay ? MaxRateLimitDelay : wait;

                    logger.LogWarning(
                        "LLM provider {Provider} model {Model} rate-limited on attempt {Attempt}; "
                        + "retrying after {RetryDelayMs}ms",
                        _options.Provider,
                        _options.Model,
                        attempt,
                        (int)wait.TotalMilliseconds);

                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (translated is LlmRateLimitException)
                {
                    StartCooldown();
                }

                throw translated;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                logger.LogWarning(
                    "LLM request to provider '{Provider}' timed out after {TimeoutSeconds}s.",
                    _options.Provider,
                    _options.RequestTimeoutSeconds);

                throw new LlmTimeoutException(
                    $"The LLM provider did not respond within {_options.RequestTimeoutSeconds} seconds.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(
                    exception,
                    "LLM provider '{Provider}' could not be reached.",
                    _options.Provider);

                throw new LlmUnavailableException(
                    "The LLM provider could not be reached.",
                    exception);
            }
        }
    }

    private TimeSpan? GetCooldownRemaining()
    {
        lock (_cooldownGate)
        {
            if (_cooldownUntil is not { } until)
            {
                return null;
            }

            var remaining = until - _utcNow();
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }

            _cooldownUntil = null;
            return null;
        }
    }

    private void StartCooldown()
    {
        var until = _utcNow().AddSeconds(_options.RateLimitCooldownSeconds);
        lock (_cooldownGate)
        {
            _cooldownUntil = until;
        }

        logger.LogWarning(
            "LLM provider {Provider} model {Model} entered cooldown until {CooldownUntil} after rate-limit retries.",
            _options.Provider,
            _options.Model,
            until);
    }

    /// <summary>
    /// Resolves the endpoint for a provider. Python: <c>create_provider</c>.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">The provider name is not supported.</exception>
    internal static string ResolveBaseUrl(string provider, string? configuredBaseUrl)
    {
        var explicitUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? null
            : configuredBaseUrl.TrimEnd('/');

        return provider?.Trim().ToLowerInvariant() switch
        {
            "openai" or "azure_openai" => explicitUrl ?? OpenAiBaseUrl,
            "groq" => explicitUrl ?? GroqBaseUrl,
            _ => throw new LlmNotConfiguredException(
                $"Unsupported LLM provider: '{provider}'. Supported: openai, azure_openai, groq"),
        };
    }

    /// <summary>Builds the chat-completions request body. Python: <c>OpenAIProvider.generate</c>.</summary>
    private JsonObject BuildRequestBody(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools)
    {
        var wireMessages = new JsonArray();
        foreach (var message in messages)
        {
            wireMessages.Add(message.ToWire());
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = wireMessages,
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = _options.MaxTokens,
        };

        if (tools is { Count: > 0 })
        {
            var wireTools = new JsonArray();
            foreach (var tool in tools)
            {
                wireTools.Add(tool.ToOpenAiSchema());
            }

            body["tools"] = wireTools;

            // "auto" lets the model decide whether evidence is needed; the agent never
            // forces a specific tool.
            body["tool_choice"] = "auto";
        }

        return body;
    }

    /// <summary>Maps a provider response onto the provider-independent completion shape.</summary>
    private static LlmCompletion ParseCompletion(string payload)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new LlmException("The LLM provider returned a malformed response.", exception);
        }

        if (root?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not { } choice)
        {
            // A response without choices is not an error, just nothing to act on.
            return LlmCompletion.Empty;
        }

        var message = choice["message"];
        var content = message?["content"]?.GetValue<string>();
        var finishReason = choice["finish_reason"]?.GetValue<string>();

        var toolCalls = new List<LlmToolCall>();
        if (message?["tool_calls"] is JsonArray rawCalls)
        {
            foreach (var rawCall in rawCalls)
            {
                var function = rawCall?["function"];
                var name = function?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                toolCalls.Add(new LlmToolCall(
                    rawCall?["id"]?.GetValue<string>() ?? string.Empty,
                    name,
                    ReadArguments(function?["arguments"])));
            }
        }

        return new LlmCompletion(
            content,
            toolCalls,
            string.IsNullOrWhiteSpace(finishReason) ? "stop" : finishReason);
    }

    /// <summary>
    /// Reads tool arguments as raw JSON text.
    /// </summary>
    /// <remarks>
    /// The specification says <c>arguments</c> is a JSON-encoded string, but some
    /// OpenAI-compatible providers emit an object instead. Both are accepted so a provider
    /// quirk does not fail the whole investigation.
    /// </remarks>
    private static string ReadArguments(JsonNode? arguments) => arguments switch
    {
        null => "{}",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => arguments.ToJsonString(),
    };

    /// <summary>
    /// Translates an unsuccessful provider response into the SQLPulse exception hierarchy.
    /// Python: <c>_translate_provider_error</c>.
    /// </summary>
    /// <remarks>
    /// Status code alone is not enough: providers report an exceeded token-per-minute quota
    /// as 429 and an exceeded context window as 400, both of which are fixed by sending
    /// less rather than by waiting, so the body is inspected as well.
    /// </remarks>
    private static LlmException TranslateError(
        HttpResponseMessage response,
        string payload,
        string provider,
        ILogger logger)
    {
        var status = response.StatusCode;
        var detail = SummarizeError(payload);
        var lower = detail.ToLowerInvariant();

        logger.LogWarning(
            "LLM provider '{Provider}' returned {StatusCode}: {Detail}",
            provider,
            (int)status,
            detail);

        if (status == HttpStatusCode.RequestEntityTooLarge || lower.Contains("request too large"))
        {
            return new LlmPayloadTooLargeException(
                $"The LLM provider rejected the request as too large: {detail}");
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            // Providers can state the wait twice and disagree: Groq rounds the
            // Retry-After header down to a whole second while its body carries the real
            // figure. Retrying on the shorter of the two just fails again, so both are
            // read and the longer wins.
            var retryAfter = LongerOf(ReadRetryAfter(response), ParseRetryHint(detail));

            // A token-per-minute rejection has two distinct causes needing opposite fixes.
            // If the request cannot be shown to fit inside the quota, it is treated as an
            // oversized payload as before. Only when the provider's own numbers prove the
            // request would fit - meaning the rolling window is merely full - is it a wait
            // problem, where compacting achieves nothing and only a retry can succeed.
            if ((lower.Contains("tokens per minute") || lower.Contains("tpm"))
                && !RequestFitsWithinQuota(detail))
            {
                return new LlmPayloadTooLargeException(
                    $"The LLM provider's token-per-minute limit was exceeded: {detail}");
            }

            return new LlmRateLimitException(
                $"The LLM provider rate limit was exceeded: {detail}",
                retryAfter);
        }

        if (status == HttpStatusCode.BadRequest && lower.Contains("context") && lower.Contains("length"))
        {
            return new LlmPayloadTooLargeException(
                $"The request exceeded the model's context length: {detail}");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Deliberately says nothing about the key itself.
            return new LlmAuthenticationException("The LLM provider rejected the configured credentials.");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            return new LlmTimeoutException($"The LLM provider timed out: {detail}");
        }

        if ((int)status >= 500)
        {
            return new LlmUnavailableException(
                $"The LLM provider is unavailable ({(int)status}): {detail}");
        }

        return new LlmInvalidRequestException($"The LLM provider returned {(int)status}: {detail}");
    }

    /// <summary>Extracts a short, credential-free description of a provider error.</summary>
    /// <remarks>
    /// Error bodies can echo part of the request, so the text is sanitized and capped
    /// before it reaches a log or an exception message.
    /// </remarks>
    private static string SummarizeError(string payload)
    {
        var detail = payload;

        try
        {
            if (JsonNode.Parse(payload)?["error"] is { } error)
            {
                detail = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall back to the raw body.
        }

        detail = LlmSanitizer.SanitizeText(detail).Trim();

        return detail.Length <= MaxErrorDetailChars
            ? detail
            : detail[..MaxErrorDetailChars];
    }

    /// <summary>Reads the provider's <c>Retry-After</c> hint, if it sent one.</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Matches a provider quota report such as
    /// <c>"Limit 8000, Used 4048, Requested 4067"</c>.
    /// </summary>
    [GeneratedRegex(
        @"limit\s+(?<limit>\d+).*?requested\s+(?<requested>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TokenQuotaPattern();

    /// <summary>
    /// Matches a provider retry hint such as <c>"Please try again in 862.5ms"</c>
    /// or <c>"try again in 1.5s"</c>.
    /// </summary>
    [GeneratedRegex(
        @"try\s+again\s+in\s+(?<value>\d+(?:\.\d+)?)\s*(?<unit>ms|s)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RetryHintPattern();

    /// <summary>
    /// True only when the provider's own numbers prove the request would fit inside the
    /// quota, meaning the rejection came from a momentarily full rolling window rather
    /// than from an oversized request.
    /// </summary>
    /// <remarks>
    /// Returns false when the numbers are absent or unparseable, so an unrecognised quota
    /// message keeps the conservative "send less" handling.
    /// </remarks>
    internal static bool RequestFitsWithinQuota(string detail)
    {
        var match = TokenQuotaPattern().Match(detail);

        return match.Success
            && long.TryParse(match.Groups["limit"].Value, out var limit)
            && long.TryParse(match.Groups["requested"].Value, out var requested)
            && requested <= limit;
    }

    /// <summary>Picks the longer of two optional waits, so neither statement is undercut.</summary>
    internal static TimeSpan? LongerOf(TimeSpan? first, TimeSpan? second) =>
        (first, second) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };

    /// <summary>Reads a retry delay embedded in the provider's error text.</summary>
    /// <remarks>
    /// Groq reports the wait in the message body rather than a <c>Retry-After</c> header,
    /// so the text is the only source of the hint.
    /// </remarks>
    internal static TimeSpan? ParseRetryHint(string detail)
    {
        var match = RetryHintPattern().Match(detail);

        if (!match.Success
            || !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        var wait = string.Equals(match.Groups["unit"].Value, "ms", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMilliseconds(value)
            : TimeSpan.FromSeconds(value);

        return wait > TimeSpan.Zero ? wait : null;
    }
}
