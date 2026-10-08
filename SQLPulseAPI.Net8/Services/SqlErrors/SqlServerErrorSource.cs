using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Database;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

public sealed class SqlServerErrorSource : ISqlErrorSource
{
    private const string DefaultSessionName = "system_health";

    private readonly ISqlQueryExecutor _executor;
    private readonly IOptions<SqlErrorOptions> _sqlErrorOptions;
    private readonly ILogger<SqlServerErrorSource> _logger;
    public string Name => "sql_server";

    public SqlErrorSourceKind Kind => SqlErrorSourceKind.SqlServer;

    public SqlServerErrorSource(
        ISqlQueryExecutor executor,
        IOptions<SqlErrorOptions> sqlErrorOptions,
        ILogger<SqlServerErrorSource> logger)
    {
        _executor = executor;
        _sqlErrorOptions = sqlErrorOptions;
        _logger = logger;
    }

    public async Task<SqlErrorSourceResult> QueryAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        var options = _sqlErrorOptions.Value;

        if (!options.EnableSqlServerErrorSource)
        {
            return SqlErrorSourceResult.NotConfigured(
                "SQL Server error source is disabled.");
        }

        if (!_executor.IsConfigured)
        {
            return SqlErrorSourceResult.NotConfigured(
                "SQL Server is not configured.");
        }

        var sessionName = string.IsNullOrWhiteSpace(options.ExtendedEventsSessionName)
            ? DefaultSessionName
            : options.ExtendedEventsSessionName.Trim();

        try
        {
            var sessionState = await GetSessionStateAsync(
                sessionName,
                cancellationToken);

            if (!sessionState.Exists)
            {
                return SqlErrorSourceResult.NotConfigured(
                    $"Extended Events session '{sessionName}' is not available.");
            }

            var events = await ReadEventsAsync(
                sessionName,
                query,
                options.CaptureParameterValues,
                cancellationToken);

            return SqlErrorSourceResult.Available(events);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "SQL Server error source failed for session {SessionName}",
                sessionName);

            return SqlErrorSourceResult.Failed(
                "SQL Server error telemetry could not be read.");
        }
    }

    private async Task<(bool Exists, bool Running)> GetSessionStateAsync(
        string sessionName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CAST(
                    CASE
                        WHEN EXISTS
                        (
                            SELECT 1
                            FROM sys.server_event_sessions
                            WHERE name = @p0
                        )
                        THEN 1
                        ELSE 0
                    END
                    AS bit) AS session_exists,
                CAST(
                    CASE
                        WHEN EXISTS
                        (
                            SELECT 1
                            FROM sys.dm_xe_sessions
                            WHERE name = @p0
                        )
                        THEN 1
                        ELSE 0
                    END
                    AS bit) AS session_running
            """;

        var rows = await _executor.ExecuteReadQueryAsync(
            sql,
            null,
            new object?[] { sessionName },
            cancellationToken);

        if (rows.Count == 0)
        {
            return (false, false);
        }

        var row = rows[0];

        var existsValue = row.GetString("session_exists");
        var runningValue = row.GetString("session_running");

        var exists = existsValue == "1" ||
                    string.Equals(existsValue, "true", StringComparison.OrdinalIgnoreCase);

        var running = runningValue == "1" ||
                    string.Equals(runningValue, "true", StringComparison.OrdinalIgnoreCase);

        return (exists, running);
    }

    private async Task<IReadOnlyList<RawSqlErrorEvent>> ReadEventsAsync(
        string sessionName,
        SqlErrorQuery query,
        bool captureParameterValues,
        CancellationToken cancellationToken)
    {
        /*
         * This query reads from the session's in-memory ring buffer.
         *
         * IMPORTANT:
         * - No CREATE/ALTER/DROP EVENT SESSION
         * - No start/stop of the session
         * - Read-only only
         *
         * The event payload is intentionally mapped conservatively because
         * system_health payloads differ by event type and SQL Server version.
         */

        // Read the bounded ring-buffer payload once. Shredding it with multiple SQL XML
        // value()/nodes() calls is needlessly expensive on busy system_health sessions and
        // can consume the command timeout before the event filter is applied. The payload is
        // parsed below after it has crossed the read-only database boundary.
        const string sql = """
            SELECT CAST(t.target_data AS nvarchar(max)) AS target_data
            FROM sys.dm_xe_sessions AS s
            INNER JOIN sys.dm_xe_session_targets AS t
                ON s.address = t.event_session_address
            WHERE s.name = @p0
              AND t.target_name = N'ring_buffer'
            """;

        var parameters = new object?[]
        {
            sessionName
        };

        var rows = await _executor.ExecuteReadQueryAsync(
            sql,
            null,
            parameters,
            cancellationToken);

        var targetData = rows
            .Select(row => ReadNullableString(row, "target_data"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(targetData))
        {
            return [];
        }

        var document = XDocument.Parse(targetData, LoadOptions.None);
        var results = new List<RawSqlErrorEvent>();

        // SQL Server can emit ring-buffer XML with the event-data namespace on some
        // versions/configurations. Match by local name so the payload is not silently
        // reduced to blank fields when that namespace is present.
        foreach (var eventNode in document.Descendants()
                     .Where(element => element.Name.LocalName == "event"))
        {
            var eventName = (string?)eventNode.Attribute("name");

            if (!IsRelevantErrorEvent(eventName) ||
                !TryReadTimestamp((string?)eventNode.Attribute("timestamp"), out var timestamp) ||
                timestamp < query.StartTime ||
                timestamp >= query.EndTime)
            {
                continue;
            }

            var database = ReadEventValue(eventNode, "database_name");

            if (!string.IsNullOrWhiteSpace(query.Database) &&
                !string.Equals(
                    database,
                    query.Database,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(
                new RawSqlErrorEvent
                {
                    Timestamp = timestamp,
                    Server = _executor.ServerHost,
                    Database = database,
                    Application = ReadEventValue(eventNode, "client_app_name"),
                    Host = ReadEventValue(eventNode, "client_hostname"),
                    SessionIdentifier = ReadEventValue(eventNode, "session_id"),
                    SessionId = ReadEventInt(eventNode, "session_id"),
                    SqlErrorNumber = ReadEventInt(eventNode, "error_number"),
                    SqlErrorState = ReadEventInt(eventNode, "state"),
                    SqlErrorClass = ReadEventInt(eventNode, "severity"),
                    StoredProcedure = ReadEventValue(eventNode, "object_name")
                        ?? ReadEventValue(eventNode, "stored_procedure"),
                    QueryHash = ReadEventValue(eventNode, "query_hash"),
                    Message = ReadEventValue(eventNode, "message")
                        ?? ReadEventXmlValue(eventNode, "xml_report"),
                    DurationMs = ReadEventDurationMs(eventNode),
                    SqlText = ReadEventValue(eventNode, "sql_text"),
                    EventName = eventName,
                    SourceName = sessionName,
                    ApplicationTimeoutReported = false,
                    Parameters = captureParameterValues
                        ? ReadParameters(eventNode)
                        : EmptyParameters,
                });
        }

        return results.OrderByDescending(result => result.Timestamp).ToList();
    }

    private static bool IsRelevantErrorEvent(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return false;
        }

        return eventName.Contains("error", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("attention", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadNullableString(
        SqlRow row,
        string column)
    {
        try
        {
            return row.GetString(column);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadEventValue(XElement eventNode, string name)
    {
        return eventNode.Elements()
            .Where(element => element.Name.LocalName is "data" or "action")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal))
            ?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "value")
            ?.Value;
    }

    private static string? ReadEventXmlValue(XElement eventNode, string name)
    {
        var value = eventNode.Elements()
            .Where(element => element.Name.LocalName == "data")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal))
            ?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "value");

        return value is null
            ? null
            : string.Concat(value.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
    }

    private static int? ReadEventInt(XElement eventNode, string name) =>
        int.TryParse(
            ReadEventValue(eventNode, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static double? ReadEventDurationMs(XElement eventNode)
    {
        var value = ReadEventValue(eventNode, "duration_ms");
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return ms;
        }

        // Extended Events reports duration in microseconds for completion events.
        return double.TryParse(
            ReadEventValue(eventNode, "duration"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var microseconds)
            ? microseconds / 1000d
            : null;
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyParameters =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ParameterAssignment = new(
        @"(?<name>@[A-Za-z_][A-Za-z0-9_#]*|[A-Za-z_][A-Za-z0-9_#]*)\s*=\s*(?<value>[^;,\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyDictionary<string, string?> ReadParameters(XElement eventNode)
    {
        var parameterElements = eventNode
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "parameter", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var name = (string?)element.Attribute("name")
                    ?? (string?)element.Attribute("parameter_name");
                var value = element.Element("value")?.Value ?? element.Value;
                return (name, value);
            });

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parameterElements)
        {
            AddParameter(result, name, value);
        }

        foreach (var payload in eventNode
            .Descendants()
            .Where(element => element.Name.LocalName is "data" or "action")
            .Where(element => IsParameterPayload((string?)element.Attribute("name"))))
        {
            var valueElement = payload.Element("value");
            foreach (var parameter in valueElement?.Descendants("parameter") ?? [])
            {
                AddParameter(
                    result,
                    (string?)parameter.Attribute("name") ?? (string?)parameter.Attribute("parameter_name"),
                    parameter.Element("value")?.Value ?? parameter.Value);
            }

            var text = valueElement is null
                ? null
                : string.Concat(valueElement.Nodes().Where(node => node is XText).Select(node => node.ToString()));
            foreach (Match match in ParameterAssignment.Matches(text ?? string.Empty))
            {
                AddParameter(result, match.Groups["name"].Value, match.Groups["value"].Value);
            }
        }

        return result;
    }

    private static bool IsParameterPayload(string? name) => name is
        "parameter_values" or "parameters" or "parameter_list" or "execution_parameters";

    private static void AddParameter(
        IDictionary<string, string?> result,
        string? name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || result.Count >= 100)
        {
            return;
        }

        var key = name.Trim();
        var normalized = key.TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        result[key.StartsWith('@') ? key : $"@{key}"] =
            IsSensitiveName(normalized) || LooksLikeSecret(value)
                ? "[REDACTED]"
                : value is null ? null : value.Trim()[..Math.Min(value.Trim().Length, 512)];
    }

    private static bool IsSensitiveName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("passwd", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pwd", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSecret(string? value) =>
        value is not null && (value.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("pwd=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("connection string", StringComparison.OrdinalIgnoreCase));

    private static bool TryReadTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }
}
