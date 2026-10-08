using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Guard rails for the read-only investigation agent
/// (Python: <c>MAX_TOOL_RESULT_CHARS</c>, <c>MAX_AGENT_TOOL_CALLS</c>,
/// <c>MAX_AGENT_EXECUTION_SECONDS</c>).
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Maximum characters of a tool result handed back to the model.</summary>
    [Range(100, 1_000_000)]
    public int MaxToolResultChars { get; set; } = 4_000;

    /// <summary>Maximum tool calls allowed in a single investigation.</summary>
    [Range(1, 100)]
    public int MaxToolCalls { get; set; } = 10;

    /// <summary>Wall-clock budget for a single investigation.</summary>
    [Range(1, 3600)]
    public int MaxExecutionSeconds { get; set; } = 60;
}
