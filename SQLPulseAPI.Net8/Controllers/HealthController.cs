using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Responses;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// Liveness endpoint. Mirrors <c>GET /health</c> in the Python implementation.
/// </summary>
[ApiController]
public sealed class HealthController(IOptions<ApplicationOptions> options) : ControllerBase
{
    private readonly ApplicationOptions _application = options.Value;

    /// <summary>Reports whether SQLPulse itself is running.</summary>
    /// <remarks>Does not touch SQL Server, so it stays green while the database is unreachable.</remarks>
    [HttpGet("/health")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse
    {
        Status = "healthy",
        Application = _application.Name,
        Version = _application.Version,
    });
}
