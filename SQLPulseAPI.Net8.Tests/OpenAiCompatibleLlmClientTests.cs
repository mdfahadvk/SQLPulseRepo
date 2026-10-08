using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Behaviour of the OpenAI-compatible LLM client: request shaping, response mapping, and
/// the translation of provider failures into the outcomes SQLPulse reacts to differently.
/// Every test runs against a stub handler — no test ever contacts a real provider.
/// </summary>
public class OpenAiCompatibleLlmClientTests
{
    private const string ApiKey = "sk-test-do-not-log-me";

    private static readonly IReadOnlyList<LlmMessage> Conversation =
        [LlmMessage.System("You are SQLPulse."), LlmMessage.User("Is the server healthy?")];

    private static LlmOptions Options(string provider = "openai", string baseUrl = "", string apiKey = ApiKey) =>
        new()
        {
            Provider = provider,
            ApiKey = apiKey,
            Model = "gpt-4o",
            BaseUrl = baseUrl,
            Temperature = 0.1,
            MaxTokens = 1500,
            RequestTimeoutSeconds = 30,
        };

    private static OpenAiCompatibleLlmClient Client(
        HttpMessageHandler handler,
        LlmOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<OpenAiCompatibleLlmClient>.Instance,
            delay ?? (static (_, _) => Task.CompletedTask),
            utcNow);

    private static string Completion(
        string? content = "All good.",
        string? finishReason = "stop",
        JsonArray? toolCalls = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (toolCalls is not null)
        {
            message["tool_calls"] = toolCalls;
        }

        var choice = new JsonObject { ["message"] = message };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        return new JsonObject { ["choices"] = new JsonArray(choice) }.ToJsonString();
    }

    private static JsonArray ToolCall(string name, string arguments) =>
        new(new JsonObject
        {
            ["id"] = "call_1",
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        });

    private static LlmToolDefinition SampleTool() => new()
    {
        Name = "get_server_health",
        Description = "Collect a server-wide performance snapshot.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        },
    };

    // --- Configuration -------------------------------------------------------

    [Fact]
    public async Task A_missing_api_key_is_reported_as_not_configured()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());
        var client = Client(handler, Options(apiKey: string.Empty));

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<LlmNotConfiguredException>(() => client.CompleteAsync(Conversation));

        // Nothing is sent when there is nothing to authenticate with.
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task An_unsupported_provider_is_reported_as_not_configured()
    {
        var client = Client(
            StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion()),
            Options(provider: "anthropic"));

        var exception = await Assert.ThrowsAsync<LlmNotConfiguredException>(
            () => client.CompleteAsync(Conversation));

        Assert.Contains("openai, azure_openai, groq", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groq_uses_its_openai_compatible_endpoint_by_default()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler, Options(provider: "groq")).CompleteAsync(Conversation);

        Assert.Equal(
            $"{OpenAiCompatibleLlmClient.GroqBaseUrl}/chat/completions",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_configured_base_url_overrides_the_provider_default()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler, Options(provider: "groq", baseUrl: "https://gateway.internal/v1/"))
            .CompleteAsync(Conversation);

        Assert.Equal(
            "https://gateway.internal/v1/chat/completions",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // --- Request shaping -----------------------------------------------------

    [Fact]
    public async Task The_request_carries_the_configured_model_and_sampling_settings()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler).CompleteAsync(Conversation);

        var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();

        Assert.Equal("gpt-4o", body["model"]!.GetValue<string>());
        Assert.Equal(0.1, body["temperature"]!.GetValue<double>());
        Assert.Equal(1500, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(2, body["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task Tools_are_offered_with_automatic_selection()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler).CompleteAsync(Conversation, [SampleTool()]);

        var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();

        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Equal(
            "get_server_health",
            body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_tools_means_no_tool_fields_at_all()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler).CompleteAsync(Conversation);

        var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();

        // Providers reject an empty tool list, so the fields are omitted rather than blank.
        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("tool_choice"));
    }

    /// <summary>The key authenticates the request and must never appear in the payload.</summary>
    [Fact]
    public async Task The_api_key_is_sent_as_a_header_and_never_in_the_body()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion());

        await Client(handler).CompleteAsync(Conversation);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain(ApiKey, handler.LastRequestBody!, StringComparison.Ordinal);
    }

    // --- Response mapping ----------------------------------------------------

    [Fact]
    public async Task Content_and_finish_reason_are_mapped()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Completion("Healthy.", "stop"));

        var completion = await Client(handler).CompleteAsync(Conversation);

        Assert.Equal("Healthy.", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.False(completion.HasToolCalls);
    }

    [Fact]
    public async Task Tool_calls_are_mapped_with_their_raw_arguments()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            Completion(null, "tool_calls", ToolCall("get_blocking", "{\"database\":\"AppDb\"}")));

        var completion = await Client(handler).CompleteAsync(Conversation, [SampleTool()]);

        Assert.True(completion.HasToolCalls);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_blocking", call.FunctionName);
        Assert.Equal("{\"database\":\"AppDb\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task A_missing_finish_reason_defaults_to_stop()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            Completion("Healthy.", finishReason: null));

        Assert.Equal("stop", (await Client(handler).CompleteAsync(Conversation)).FinishReason);
    }

    [Fact]
    public async Task A_response_without_choices_yields_an_empty_completion()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "{\"choices\":[]}");

        var completion = await Client(handler).CompleteAsync(Conversation);

        Assert.Null(completion.Content);
        Assert.Empty(completion.ToolCalls);
    }

    [Fact]
    public async Task A_malformed_response_is_reported_rather_than_crashing()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "not json at all");

        await Assert.ThrowsAsync<LlmException>(() => Client(handler).CompleteAsync(Conversation));
    }

    // --- Failure translation -------------------------------------------------

    [Fact]
    public async Task A_rate_limit_is_distinguished_from_an_oversized_request()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Rate limit reached for requests\"}}"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<LlmRateLimitException>(
            () => Client(handler).CompleteAsync(Conversation));

        Assert.Equal(TimeSpan.FromSeconds(20), exception.RetryAfter);
    }

    /// <summary>
    /// A token-per-minute rejection also arrives as 429, but waiting will not fix it: the
    /// request itself is too big, so it is reported as such.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limit reached for tokens per minute (TPM)")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Payload too large")]
    [InlineData(HttpStatusCode.BadRequest, "This model's maximum context length is 8192 tokens")]
    [InlineData(HttpStatusCode.BadRequest, "Request too large for model")]
    public async Task Oversized_requests_are_reported_as_payload_too_large(
        HttpStatusCode statusCode,
        string providerMessage)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = providerMessage },
        }.ToJsonString();

        var handler = StubHttpMessageHandler.Returning(statusCode, body);

        await Assert.ThrowsAsync<LlmPayloadTooLargeException>(
            () => Client(handler).CompleteAsync(Conversation));
    }

    /// <summary>
    /// A token-per-minute rejection whose own numbers show the request would have fit is a
    /// momentarily full rolling window, not an oversized request. Compacting cannot fix it,
    /// so it must surface as a retryable rate limit or every multi-call investigation fails
    /// on a small per-minute quota.
    /// </summary>
    [Fact]
    public async Task A_full_quota_window_is_retryable_rather_than_oversized()
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] =
                    "Rate limit reached for model `openai/gpt-oss-120b` on tokens per minute "
                    + "(TPM): Limit 8000, Used 4048, Requested 4067. "
                    + "Please try again in 862.5ms.",
            },
        }.ToJsonString();

        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, body);

        var exception = await Assert.ThrowsAsync<LlmRateLimitException>(
            () => Client(handler).CompleteAsync(Conversation));

        Assert.Equal(TimeSpan.FromMilliseconds(862.5), exception.RetryAfter);
    }

    [Fact]
    public async Task Rate_limits_retry_three_times_with_the_documented_delays()
    {
        var waits = new List<TimeSpan>();
        var requestNumber = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestNumber++;
            if (requestNumber <= 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"try again\"}}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Completion()),
            });
        });

        var completion = await Client(handler, delay: (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        }).CompleteAsync(Conversation);

        Assert.Equal("All good.", completion.Content);
        Assert.Equal(4, handler.RequestCount);
        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            waits);
    }

    [Fact]
    public async Task Exhausted_rate_limits_enter_cooldown_without_an_extra_provider_call()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"try again\"}}");
        var client = Client(handler, delay: static (_, _) => Task.CompletedTask, utcNow: () => now);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.CompleteAsync(Conversation));
        var exception = await Assert.ThrowsAsync<LlmRateLimitException>(() => client.CompleteAsync(Conversation));

        Assert.Equal(4, handler.RequestCount);
        Assert.Equal("AI provider is temporarily rate-limited. Please try again shortly.", exception.Message);
    }

    [Fact]
    public async Task Retry_after_is_honoured_but_capped()
    {
        var waits = new List<TimeSpan>();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"message\":\"try again\"}}"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });

        await Assert.ThrowsAsync<LlmRateLimitException>(() => Client(
            handler,
            delay: (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            }).CompleteAsync(Conversation));

        Assert.Equal(3, waits.Count);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(30), wait));
    }

    /// <summary>
    /// A request larger than the entire quota cannot be fixed by waiting, so it keeps the
    /// oversized-payload handling that makes the caller send less.
    /// </summary>
    [Fact]
    public async Task A_request_larger_than_the_whole_quota_is_still_oversized()
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] =
                    "Rate limit reached on tokens per minute (TPM): "
                    + "Limit 8000, Used 0, Requested 12000.",
            },
        }.ToJsonString();

        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, body);

        await Assert.ThrowsAsync<LlmPayloadTooLargeException>(
            () => Client(handler).CompleteAsync(Conversation));
    }

    [Theory]
    [InlineData("Please try again in 862.5ms.", 862.5)]
    [InlineData("please try again in 1.5s", 1500)]
    [InlineData("Try again in 2s", 2000)]
    public void A_retry_hint_is_read_from_the_provider_message(string detail, double expectedMs)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            OpenAiCompatibleLlmClient.ParseRetryHint(detail));
    }

    /// <summary>
    /// Groq rounds its <c>Retry-After</c> header down to a whole second while the body
    /// carries the real figure. Trusting the header alone retries too early and fails for
    /// the same reason, so the longer of the two has to win.
    /// </summary>
    [Fact]
    public async Task The_longer_of_two_disagreeing_retry_hints_is_used()
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] =
                    "Rate limit reached on tokens per minute (TPM): "
                    + "Limit 8000, Used 4353, Requested 4197. Please try again in 4.125s.",
            },
        }.ToJsonString();

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<LlmRateLimitException>(
            () => Client(handler).CompleteAsync(Conversation));

        Assert.Equal(TimeSpan.FromSeconds(4.125), exception.RetryAfter);
    }

    [Theory]
    [InlineData("Rate limit reached for tokens per minute (TPM)")]
    [InlineData("no numbers here at all")]
    public void An_unparseable_quota_message_is_not_assumed_to_fit(string detail)
    {
        Assert.False(OpenAiCompatibleLlmClient.RequestFitsWithinQuota(detail));
    }

    [Fact]
    public async Task A_server_error_is_reported_as_provider_unavailable()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.InternalServerError,
            "{\"error\":{\"message\":\"Internal error\"}}");

        await Assert.ThrowsAsync<LlmUnavailableException>(() => Client(handler).CompleteAsync(Conversation));
    }

    [Fact]
    public async Task An_unreachable_provider_is_reported_as_unavailable()
    {
        await Assert.ThrowsAsync<LlmUnavailableException>(
            () => Client(StubHttpMessageHandler.Unreachable()).CompleteAsync(Conversation));
    }

    [Fact]
    public async Task A_provider_that_never_responds_is_reported_as_a_timeout()
    {
        var options = Options();
        options.RequestTimeoutSeconds = 1;

        await Assert.ThrowsAsync<LlmTimeoutException>(
            () => Client(StubHttpMessageHandler.NeverResponds(), options).CompleteAsync(Conversation));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_disguised_as_a_provider_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(StubHttpMessageHandler.NeverResponds())
                .CompleteAsync(Conversation, null, cancellation.Token));
    }

    /// <summary>
    /// Error bodies can echo part of the request, so the detail copied into an exception is
    /// sanitized before it can reach a log or an API response.
    /// </summary>
    [Fact]
    public async Task Credentials_echoed_in_a_provider_error_are_redacted()
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = "Rejected input containing Password=letmein" },
        }.ToJsonString();

        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, body);

        var exception = await Assert.ThrowsAsync<LlmRateLimitException>(
            () => Client(handler).CompleteAsync(Conversation));

        Assert.DoesNotContain("letmein", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LlmSanitizer.Redacted, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_credential_is_reported_without_repeating_it()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.Unauthorized,
            $"{{\"error\":{{\"message\":\"Incorrect API key provided: {ApiKey}\"}}}}");

        var exception = await Assert.ThrowsAsync<LlmAuthenticationException>(
            () => Client(handler).CompleteAsync(Conversation));

        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
    }
}
