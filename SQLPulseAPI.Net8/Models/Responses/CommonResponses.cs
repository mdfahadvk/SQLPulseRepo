using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Responses;

/// <summary>Reports whether SQLPulse itself is running. Python: <c>HealthResponse</c>.</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";

    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>Result of a SQL Server connectivity probe. Python: <c>ConnectionTestResponse</c>.</summary>
public sealed class ConnectionTestResponse
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>A single discovered database. Python: <c>DatabaseEntry</c>.</summary>
public sealed class DatabaseEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Databases discovered on the monitored server. Python: <c>DatabaseListResponse</c>.</summary>
public sealed class DatabaseListResponse
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("databases")]
    public List<DatabaseEntry> Databases { get; set; } = [];
}

/// <summary>FastAPI-compatible error envelope (<c>{"detail": "..."}</c>).</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Root discovery document listing the available resources.</summary>
public sealed class RootResponse
{
    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("endpoints")]
    public Dictionary<string, string> Endpoints { get; set; } = [];
}
