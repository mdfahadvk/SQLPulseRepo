using Microsoft.AspNetCore.Mvc;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Database;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// Connectivity and database discovery.
/// Mirrors <c>GET /database/connection-test</c> and <c>GET /api/databases</c> in the
/// Python implementation.
/// </summary>
[ApiController]
public sealed class DatabaseController(
    ISqlQueryExecutor executor,
    IDatabaseDiscoveryService discovery,
    ILogger<DatabaseController> logger) : ControllerBase
{
    /// <summary>Message returned when the connectivity probe succeeds.</summary>
    public const string ConnectedMessage = "Successfully connected to SQL Server.";

    /// <summary>Message returned when the connectivity probe fails.</summary>
    public const string NotConnectedMessage =
        "Connection to SQL Server failed. Check server availability and credentials.";

    /// <summary>Message returned when the probe itself throws.</summary>
    public const string UnexpectedErrorMessage = "Connection test encountered an unexpected error.";

    /// <summary>Detail returned when the database list cannot be retrieved.</summary>
    public const string DiscoveryFailedMessage = "Unable to query SQL Server for database list.";

    /// <summary>
    /// Verifies SQL Server connectivity without exposing credentials.
    /// </summary>
    /// <remarks>
    /// Always returns 200 — the payload reports the outcome. Failure details are
    /// logged, never returned.
    /// </remarks>
    [HttpGet("/database/connection-test")]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionTestResponse>> ConnectionTest(CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await executor.TestConnectionAsync(cancellationToken);

            return Ok(new ConnectionTestResponse
            {
                Connected = outcome.IsSuccess,
                Message = outcome.IsSuccess ? ConnectedMessage : NotConnectedMessage,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during connection test");

            return Ok(new ConnectionTestResponse
            {
                Connected = false,
                Message = UnexpectedErrorMessage,
            });
        }
    }

    /// <summary>
    /// Lists the user databases accessible to the monitoring account.
    /// </summary>
    /// <remarks>
    /// Excludes the system databases (master, model, msdb, tempdb) and returns only
    /// databases that are ONLINE and openable by the monitoring login. The cache is
    /// refreshed on every call so the list reflects the server right now.
    /// </remarks>
    [HttpGet("/api/databases")]
    [ProducesResponseType(typeof(DatabaseListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DatabaseListResponse>> ListDatabases(CancellationToken cancellationToken)
    {
        discovery.InvalidateCache();

        var result = await discovery.DiscoverDatabasesAsync(useCache: false, cancellationToken);

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Database discovery failed with status {Status}",
                result.Outcome.Status);

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse { Detail = DiscoveryFailedMessage });
        }

        return Ok(new DatabaseListResponse
        {
            Server = executor.ServerHost,
            Databases = [.. result.Databases.Select(db => new DatabaseEntry
            {
                Name = db.Name,
                Status = db.Status,
            })],
        });
    }
}
