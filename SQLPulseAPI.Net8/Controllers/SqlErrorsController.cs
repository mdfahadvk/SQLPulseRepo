using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// SQL error investigation, kept separate from performance analysis.
/// </summary>
/// <remarks>
/// <para>
/// Performance findings answer "what is slow?". These endpoints answer "what failed?".
/// They are deliberately not merged: a failed statement and a slow one need different
/// evidence and lead to different conclusions, and a timeout is only a timeout when
/// something recorded that a client gave up waiting.
/// </para>
/// <para>
/// Read-only, like the rest of SQLPulse. Nothing here enables an Extended Events session,
/// changes Query Store, ends a session or alters application configuration.
/// </para>
/// </remarks>
[ApiController]
public sealed class SqlErrorsController(
    ISqlErrorService errorService,
    ISqlErrorCorrelationService correlationService,
    IDatabaseDiscoveryService discovery,
    IOptions<SqlErrorOptions> sqlErrorOptions,
    IOptions<SqlServerOptions> sqlServerOptions) : ControllerBase
{
    /// <summary>Returned when the requested window ends at or before it starts.</summary>
    public const string EndTimeMessage = "end_time must be after start_time";

    /// <summary>Returned when the requested server is not the one SQLPulse monitors.</summary>
    public const string UnknownServerMessage =
        "server does not match the SQL Server instance SQLPulse is monitoring";

    /// <summary>Upper bound for <c>top_n</c>.</summary>
    public const int MaxTopN = 500;

    private const int DefaultTopN = 100;

    private readonly SqlErrorOptions _sqlErrors = sqlErrorOptions.Value;
    private readonly SqlServerOptions _sqlServer = sqlServerOptions.Value;

    /// <summary>
    /// Lists normalized SQL error events for a period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response always states how much SQLPulse could actually see. <c>status</c>
    /// separates <c>NO_ERRORS_FOUND</c> from <c>ERROR_SOURCE_UNAVAILABLE</c> and
    /// <c>PARTIAL_ERROR_DATA</c>, and <c>application_telemetry_available</c> reports
    /// separately whether any application feed answered, because SQL Server evidence alone
    /// cannot show what an application experienced.
    /// </para>
    /// <para>
    /// Use <c>confirmed_only=true</c> to exclude anything SQLPulse inferred from its own
    /// performance evidence rather than observed being reported.
    /// </para>
    /// </remarks>
    /// <param name="server">Monitored server. Defaults to the configured instance.</param>
    /// <param name="database">Optional database filter.</param>
    /// <param name="startTime">Start of the period (ISO 8601). Defaults to the configured lookback.</param>
    /// <param name="endTime">End of the period (ISO 8601). Defaults to now.</param>
    /// <param name="category">Optional normalized category, for example <c>TIMEOUT</c>.</param>
    /// <param name="application">Optional application filter.</param>
    /// <param name="storedProcedure">Optional stored procedure filter.</param>
    /// <param name="severity">Optional severity filter: <c>critical</c>, <c>high</c>, <c>medium</c> or <c>low</c>.</param>
    /// <param name="confirmedOnly">Exclude events SQLPulse derived rather than observed.</param>
    /// <param name="topN">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/sql-errors")]
    [ProducesResponseType(typeof(SqlErrorListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SqlErrorListResponse>> List(
        [FromQuery(Name = "server")] string? server = null,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "start_time")] DateTimeOffset? startTime = null,
        [FromQuery(Name = "end_time")] DateTimeOffset? endTime = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "application")] string? application = null,
        [FromQuery(Name = "stored_procedure")] string? storedProcedure = null,
        [FromQuery(Name = "severity")] string? severity = null,
        [FromQuery(Name = "confirmed_only")] bool confirmedOnly = false,
        [FromQuery(Name = "top_n")] int topN = DefaultTopN,
        CancellationToken cancellationToken = default)
    {
        var (failure, query) = await BuildQueryAsync(
            server, database, startTime, endTime, category, application,
            storedProcedure, severity, confirmedOnly, topN, cancellationToken);

        return failure ?? Ok(await errorService.ListAsync(query!, cancellationToken));
    }

    /// <summary>
    /// Returns aggregate SQL error counts for a period.
    /// </summary>
    /// <remarks>
    /// Confirmed timeouts and timeout candidates are counted separately, because only the
    /// first were observed by something watching the client. The same <c>status</c> and
    /// <c>application_telemetry_available</c> fields apply, so a zero total is never
    /// mistaken for a healthy period when no source could be consulted.
    /// </remarks>
    /// <param name="server">Monitored server. Defaults to the configured instance.</param>
    /// <param name="database">Optional database filter.</param>
    /// <param name="startTime">Start of the period (ISO 8601). Defaults to the configured lookback.</param>
    /// <param name="endTime">End of the period (ISO 8601). Defaults to now.</param>
    /// <param name="category">Optional normalized category, for example <c>TIMEOUT</c>.</param>
    /// <param name="application">Optional application filter.</param>
    /// <param name="storedProcedure">Optional stored procedure filter.</param>
    /// <param name="severity">Optional severity filter.</param>
    /// <param name="confirmedOnly">Exclude events SQLPulse derived rather than observed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/sql-errors/summary")]
    [ProducesResponseType(typeof(SqlErrorSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SqlErrorSummaryResponse>> Summary(
        [FromQuery(Name = "server")] string? server = null,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "start_time")] DateTimeOffset? startTime = null,
        [FromQuery(Name = "end_time")] DateTimeOffset? endTime = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "application")] string? application = null,
        [FromQuery(Name = "stored_procedure")] string? storedProcedure = null,
        [FromQuery(Name = "severity")] string? severity = null,
        [FromQuery(Name = "confirmed_only")] bool confirmedOnly = false,
        CancellationToken cancellationToken = default)
    {
        var (failure, query) = await BuildQueryAsync(
            server, database, startTime, endTime, category, application,
            storedProcedure, severity, confirmedOnly, MaxTopN, cancellationToken);

        return failure ?? Ok(await errorService.SummarizeAsync(query!, cancellationToken));
    }

    /// <summary>
    /// Returns one error event with the performance evidence observed around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The correlation is evidence, not a verdict. It reports what SQLPulse observed within
    /// the configured window either side of the error and grades how strongly that evidence
    /// refers to the same query. Coinciding in time is never reported as having caused
    /// anything.
    /// </para>
    /// <para>
    /// SQLPulse does not store error events, so an id resolves only while it remains among
    /// the recently retrieved events. An expired id returns 200 with <c>found=false</c> and
    /// an explanation, not a fabricated event.
    /// </para>
    /// </remarks>
    /// <param name="id">Identifier from a previous <c>GET /api/sql-errors</c> response.</param>
    /// <param name="correlate">Include correlated performance evidence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/sql-errors/{id}")]
    [ProducesResponseType(typeof(SqlErrorDetailResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlErrorDetailResponse>> Detail(
        [FromRoute(Name = "id")] string id,
        [FromQuery(Name = "correlate")] bool correlate = true,
        CancellationToken cancellationToken = default)
    {
        var errorEvent = errorService.Find(id);
        if (errorEvent is null)
        {
            return Ok(new SqlErrorDetailResponse
            {
                Found = false,
                Message = SqlErrorDetailResponse.NotFoundMessage,
            });
        }

        return Ok(new SqlErrorDetailResponse
        {
            Found = true,
            Error = errorEvent,
            Correlation = correlate
                ? await correlationService.CorrelateAsync(errorEvent, cancellationToken)
                : null,
        });
    }

    /// <summary>Validates the filters shared by the list and summary endpoints.</summary>
    private async Task<(ActionResult? Failure, SqlErrorQuery? Query)> BuildQueryAsync(
        string? server,
        string? database,
        DateTimeOffset? startTime,
        DateTimeOffset? endTime,
        string? category,
        string? application,
        string? storedProcedure,
        string? severity,
        bool confirmedOnly,
        int topN,
        CancellationToken cancellationToken)
    {
        // SQLPulse monitors a single instance, so a different server name is a mistake
        // worth reporting rather than a filter that would silently match nothing.
        if (!string.IsNullOrWhiteSpace(server)
            && !string.IsNullOrWhiteSpace(_sqlServer.Server)
            && !string.Equals(server, _sqlServer.Server, StringComparison.OrdinalIgnoreCase))
        {
            return (BadRequest(new ErrorResponse { Detail = UnknownServerMessage }), null);
        }

        // Discovery is the allow-list. A database name from a caller is validated before it
        // can reach a Query Store lookup.
        if (!string.IsNullOrWhiteSpace(database))
        {
            var databaseError = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);
            if (databaseError is not null)
            {
                return (BadRequest(new ErrorResponse { Detail = databaseError }), null);
            }
        }

        var end = endTime ?? DateTimeOffset.UtcNow;
        var start = startTime ?? end.AddHours(-_sqlErrors.DefaultLookbackHours);

        if (end <= start)
        {
            return (BadRequest(new ErrorResponse { Detail = EndTimeMessage }), null);
        }

        if (topN < 1 || topN > MaxTopN)
        {
            return (Unprocessable($"top_n must be between 1 and {MaxTopN}"), null);
        }

        SqlErrorCategory? categoryFilter = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!EnumMemberJsonConverter<SqlErrorCategory>.TryFromWireValue(category.Trim(), out var parsed))
            {
                var accepted = string.Join(", ", EnumMemberJsonConverter<SqlErrorCategory>.WireValues);
                return (Unprocessable($"category must be one of: {accepted}"), null);
            }

            categoryFilter = parsed;
        }

        Severity? severityFilter = null;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!EnumMemberJsonConverter<Severity>.TryFromWireValue(severity.Trim(), out var parsed))
            {
                var accepted = string.Join(", ", EnumMemberJsonConverter<Severity>.WireValues);
                return (Unprocessable($"severity must be one of: {accepted}"), null);
            }

            severityFilter = parsed;
        }

        return (null, new SqlErrorQuery(
            _sqlServer.Server,
            string.IsNullOrWhiteSpace(database) ? null : database.Trim(),
            start,
            end,
            categoryFilter,
            string.IsNullOrWhiteSpace(application) ? null : application.Trim(),
            string.IsNullOrWhiteSpace(storedProcedure) ? null : storedProcedure.Trim(),
            severityFilter,
            confirmedOnly,
            topN));
    }

    private ObjectResult Unprocessable(string detail) => StatusCode(
        StatusCodes.Status422UnprocessableEntity,
        new ErrorResponse { Detail = detail });
}
