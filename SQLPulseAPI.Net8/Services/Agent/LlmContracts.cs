using System.Text.Json.Nodes;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>
/// A single message in a conversation with the model.
/// Ported from <c>LLMMessage</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
public sealed class LlmMessage
{
    /// <summary>One of <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Message content. Null on an assistant turn that only requests tool calls.</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the assistant.</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>Identifier of the tool call this message answers. Set on <c>tool</c> messages.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Name of the tool that produced this message.</summary>
    public string? Name { get; init; }

    /// <summary>Builds a system message.</summary>
    public static LlmMessage System(string content) => new() { Role = "system", Content = content };

    /// <summary>Builds a user message.</summary>
    public static LlmMessage User(string content) => new() { Role = "user", Content = content };

    /// <summary>Builds an assistant message, optionally carrying requested tool calls.</summary>
    public static LlmMessage Assistant(string? content, IReadOnlyList<LlmToolCall>? toolCalls = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls };

    /// <summary>Builds the tool-result message that answers a specific tool call.</summary>
    public static LlmMessage Tool(string content, string toolCallId, string name) =>
        new() { Role = "tool", Content = content, ToolCallId = toolCallId, Name = name };

    /// <summary>
    /// Renders the message in the OpenAI wire format.
    /// Python: <c>LLMMessage.to_dict</c>.
    /// </summary>
    /// <remarks>
    /// Null members are omitted rather than sent as JSON null. Providers reject an
    /// explicit null <c>tool_calls</c>, and the Python reference omits them the same way.
    /// </remarks>
    public JsonObject ToWire()
    {
        var wire = new JsonObject { ["role"] = Role };

        if (Content is not null)
        {
            wire["content"] = Content;
        }

        if (ToolCalls is not null)
        {
            var calls = new JsonArray();
            foreach (var call in ToolCalls)
            {
                calls.Add(call.ToWire());
            }

            wire["tool_calls"] = calls;
        }

        if (ToolCallId is not null)
        {
            wire["tool_call_id"] = ToolCallId;
        }

        if (Name is not null)
        {
            wire["name"] = Name;
        }

        return wire;
    }
}

/// <summary>
/// A tool invocation requested by the model.
/// </summary>
/// <param name="Id">Provider-assigned call identifier, echoed back on the tool result.</param>
/// <param name="FunctionName">Name of the tool the model wants to run.</param>
/// <param name="ArgumentsJson">Raw JSON arguments exactly as the model produced them.</param>
/// <remarks>
/// Arguments stay as text here because the model can emit malformed JSON. Parsing is the
/// caller's job so a bad argument payload becomes a handled tool error rather than a
/// deserialization failure that aborts the investigation.
/// </remarks>
public sealed record LlmToolCall(string Id, string FunctionName, string ArgumentsJson)
{
    /// <summary>The only tool type OpenAI-compatible providers support.</summary>
    public const string FunctionType = "function";

    /// <summary>Renders the call in the OpenAI wire format.</summary>
    public JsonObject ToWire() => new()
    {
        ["id"] = Id,
        ["type"] = FunctionType,
        ["function"] = new JsonObject
        {
            ["name"] = FunctionName,
            ["arguments"] = ArgumentsJson,
        },
    };
}

/// <summary>
/// Schema for a tool the model may call.
/// Ported from <c>ToolDefinition</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
public sealed class LlmToolDefinition
{
    /// <summary>Tool name the model uses to invoke it.</summary>
    public required string Name { get; init; }

    /// <summary>Description the model reads when choosing a tool.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema describing the tool's parameters.</summary>
    public required JsonObject Parameters { get; init; }

    /// <summary>
    /// Renders the definition as an OpenAI function-calling schema.
    /// Python: <c>ToolDefinition.to_openai_schema</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Parameters"/> is deep-cloned because a <see cref="JsonNode"/> may only be
    /// attached to one parent, and these definitions are static and reused on every request.
    /// </remarks>
    public JsonObject ToOpenAiSchema() => new()
    {
        ["type"] = LlmToolCall.FunctionType,
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone(),
        },
    };
}

/// <summary>
/// A provider-independent model response.
/// Ported from <c>LLMResponse</c> in <c>app/agent/llm_provider.py</c>.
/// </summary>
/// <param name="Content">Assistant content, if any.</param>
/// <param name="ToolCalls">Tool calls the model requested.</param>
/// <param name="FinishReason">Provider-reported finish reason.</param>
public sealed record LlmCompletion(
    string? Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string FinishReason)
{
    /// <summary>An empty completion, used when a provider returns no choices.</summary>
    public static LlmCompletion Empty { get; } = new(null, [], "stop");

    /// <summary>True when the model asked for at least one tool call.</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;
}
