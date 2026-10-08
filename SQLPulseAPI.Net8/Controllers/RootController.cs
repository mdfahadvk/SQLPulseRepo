using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Responses;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// Root discovery document. Mirrors <c>GET /</c> in the Python implementation.
/// </summary>
[ApiController]
public sealed class RootController(IOptions<ApplicationOptions> options) : ControllerBase
{
    private readonly ApplicationOptions _application = options.Value;

    /// <summary>Lists the resources exposed by SQLPulse.</summary>
    [HttpGet("/")]
    [ProducesResponseType(typeof(RootResponse), StatusCodes.Status200OK)]
    public ActionResult<RootResponse> Get() => Ok(new RootResponse
    {
        Application = _application.Name,
        Version = _application.Version,
        Description = "Read-only SQL Server Performance Analysis Agent",
        Endpoints = new Dictionary<string, string>
        {
            ["health"] = "/health",
            ["connection_test"] = "/database/connection-test",
            ["databases"] = "/api/databases",
            ["performance_snapshot"] = "/api/performance/snapshot?scope=server",
            ["performance_analyze"] = "/api/performance/analyze?scope=server",
            ["slow_history"] = "/api/performance/slow-history?database=...&threshold_ms=30000",
            ["sql_errors"] = "/api/sql-errors",
            ["sql_errors_summary"] = "/api/sql-errors/summary",
            ["sql_error_detail"] = "/api/sql-errors/{id}",
            ["agent_investigate"] = "/api/agent/investigate",
            ["agent_investigations"] = "/api/agent/investigations",
            ["agent_investigation_detail"] = "/api/agent/investigations/{investigation_id}",
            ["query_history"] = "/api/history/query/{query_hash}?database=...",
            ["query_baseline"] = "/api/history/query/{query_hash}/baseline?database=...",
            ["docs"] = "/swagger",
        },
    });
}
