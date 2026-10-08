using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Agent;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>Reads SQL-related application exceptions and timeouts from SigNoz traces.</summary>
public sealed class SigNozErrorSource(
    SigNozTelemetryClient client,
    IOptions<SqlErrorOptions> options,
    ILogger<SigNozErrorSource> logger) : ISqlErrorSource
{
    private const int MaxMessageLength = 2_000;
    private readonly SqlErrorOptions _options = options.Value;

    public string Name => "signoz";

    public SqlErrorSourceKind Kind => SqlErrorSourceKind.ApplicationTelemetry;

    public async Task<SqlErrorSourceResult> QueryAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EffectiveSigNozEnabled)
        {
            logger.LogInformation(
                "SigNoz configuration resolution: enabled=false, database={DatabaseFilter}, resolved_application_count=0.",
                query.Database);
            return SqlErrorSourceResult.NotConfigured("SigNoz application telemetry is disabled.");
        }

        var applications = ResolveApplications(query.Database);

        if (applications.Count == 0)
        {
            logger.LogInformation(
                "SigNoz configuration resolution: enabled=true, database={DatabaseFilter}, resolved_application_count=0, configured=false.",
                query.Database);
            return new(SqlErrorSourceStatus.NotConfigured, [],
                "No applications are mapped to the selected database.",
                new ApplicationTelemetrySummary { Status = ApplicationTelemetryStatus.NotConfigured });
        }

        var baseUrlConfigured = Uri.TryCreate(_options.EffectiveSigNozBaseUrl, UriKind.Absolute, out var baseUri)
            && baseUri.Scheme is "http" or "https";
        var headerKeyConfigured = !string.IsNullOrWhiteSpace(_options.EffectiveSigNozAuthHeader);
        var accessTokenConfigured = !string.IsNullOrWhiteSpace(_options.EffectiveSigNozApiKey);
        logger.LogInformation(
            "SigNoz configuration resolution: enabled=true, database={DatabaseFilter}, resolved_applications={Applications}, service_names={ServiceNames}, configured={Configured}, base_url_configured={BaseUrlConfigured}, header_key_configured={HeaderKeyConfigured}, access_token_configured={AccessTokenConfigured}.",
            query.Database,
            string.Join(",", applications.Select(application => application.Name)),
            string.Join(",", applications.Select(application => application.ServiceName)),
            baseUrlConfigured && headerKeyConfigured && accessTokenConfigured,
            baseUrlConfigured,
            headerKeyConfigured,
            accessTokenConfigured);

        if (!baseUrlConfigured || !headerKeyConfigured || !accessTokenConfigured)
        {
            var unconfiguredStates = applications.Select(application =>
                State(application, ApplicationTelemetryStatus.NotConfigured, database: query.Database)).ToList();
            return new(SqlErrorSourceStatus.NotConfigured, [],
                "SigNoz is enabled and the application mapping resolved, but the base URL or access token is not configured.",
                new ApplicationTelemetrySummary
                {
                    Status = ApplicationTelemetryStatus.NotConfigured,
                    Applications = unconfiguredStates,
                });
        }

        var states = new List<ApplicationTelemetryApplication>();
        var events = new List<RawSqlErrorEvent>();
        foreach (var application in applications)
        {
            if (!application.TelemetryEnabled || string.IsNullOrWhiteSpace(application.ServiceName))
            {
                states.Add(State(application, ApplicationTelemetryStatus.NotConfigured, database: query.Database));
                continue;
            }

            var result = await client.QueryForServiceAsync(query.StartTime, query.EndTime, query.Database,
                application.ServiceName, cancellationToken);
            if (result.Status == SigNozQueryResultStatus.PermissionDenied)
            {
                states.Add(State(application, ApplicationTelemetryStatus.PermissionDenied, database: query.Database));
                continue;
            }

            if (result.Status == SigNozQueryResultStatus.Failed)
            {
                states.Add(State(application, ApplicationTelemetryStatus.Unavailable, database: query.Database));
                continue;
            }

            if (result.Payload is null)
            {
                states.Add(State(application, ApplicationTelemetryStatus.Unavailable, database: query.Database));
                continue;
            }

            var mapped = MapAvailable(result.Payload, application, query.Database);
            events.AddRange(mapped);
            states.Add(State(application,
                mapped.Count == 0 ? ApplicationTelemetryStatus.NoData : ApplicationTelemetryStatus.Available,
                mapped.Count, query.Database));
        }

        var usable = states.Count(state => state.Status is ApplicationTelemetryStatus.Available or ApplicationTelemetryStatus.NoData);
        var unavailable = states.Count(state => state.Status is ApplicationTelemetryStatus.Unavailable or ApplicationTelemetryStatus.PermissionDenied);
        var configured = states.Count(state => state.Status != ApplicationTelemetryStatus.NotConfigured);
        var overall = usable == 0 && unavailable > 0 ? ApplicationTelemetryStatus.Unavailable
            : unavailable > 0 ? ApplicationTelemetryStatus.Partial
            : configured > 0 && states.All(state => state.Status == ApplicationTelemetryStatus.NoData)
                ? ApplicationTelemetryStatus.NoData
            : states.Any(state => state.Status == ApplicationTelemetryStatus.Available)
                ? ApplicationTelemetryStatus.Available
                : ApplicationTelemetryStatus.NotConfigured;
        var summary = new ApplicationTelemetrySummary { Status = overall, Applications = states };
        var allPermissionDenied = states.Count > 0 && states.All(state =>
            state.Status == ApplicationTelemetryStatus.PermissionDenied);
        var status = allPermissionDenied
            ? SqlErrorSourceStatus.PermissionDenied
            : overall == ApplicationTelemetryStatus.Unavailable
                ? SqlErrorSourceStatus.Failed
            : overall == ApplicationTelemetryStatus.Partial
                ? SqlErrorSourceStatus.Partial
                : overall == ApplicationTelemetryStatus.NotConfigured
                    ? SqlErrorSourceStatus.NotConfigured
                    : SqlErrorSourceStatus.Available;
        return new(status, events,
            status == SqlErrorSourceStatus.PermissionDenied
                ? "SigNoz rejected the configured credentials."
                : status == SqlErrorSourceStatus.Failed
                    ? "SigNoz application telemetry could not be read."
                    : null,
            summary);
    }

    private List<SigNozApplication> ResolveApplications(string? database)
    {
        var applications = _options.RegisteredApplications
            .Where(application => application.Databases.Any(mappedDatabase =>
                string.Equals(mappedDatabase, database, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Keep the original single-service configuration working while deployments
        // migrate to the many-to-many registry.
        if (applications.Count == 0 && _options.RegisteredApplications.Count == 0
            && !string.IsNullOrWhiteSpace(_options.EffectiveSigNozServiceName)
            && !string.IsNullOrWhiteSpace(database))
        {
            applications.Add(new SigNozApplication
            {
                Name = _options.EffectiveSigNozServiceName,
                ServiceName = _options.EffectiveSigNozServiceName,
                Databases = [database],
            });
        }

        return applications;
    }

    private IReadOnlyList<RawSqlErrorEvent> MapAvailable(
        JsonDocument? document, SigNozApplication application, string? selectedDatabase)
    {
        if (document is null)
        {
            return Array.Empty<RawSqlErrorEvent>();
        }

        var events = new List<RawSqlErrorEvent>();
        foreach (var element in EnumerateObjects(document.RootElement))
        {
            if (!IsForConfiguredService(element, application.ServiceName))
            {
                continue;
            }

            var eventDatabase = ReadString(element, "db.namespace", "database", "db.name");
            if (!string.IsNullOrWhiteSpace(eventDatabase)
                && !string.Equals(eventDatabase, selectedDatabase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eventEnvironment = ReadString(element, "deployment.environment", "environment");
            var configuredEnvironments = application.Environment.Count > 0
                ? application.Environment
                : string.IsNullOrWhiteSpace(_options.EffectiveSigNozEnvironment)
                    ? []
                    : [_options.EffectiveSigNozEnvironment];
            if (!string.IsNullOrWhiteSpace(eventEnvironment)
                && configuredEnvironments.Count > 0
                && !configuredEnvironments.Contains(eventEnvironment, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryMap(element, application) is { } error)
            {
                events.Add(error);
            }
        }

        return events
            .GroupBy(BuildEventKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(error => error.Timestamp)
            .ToList();
    }

    private static ApplicationTelemetryApplication State(SigNozApplication application,
        ApplicationTelemetryStatus status, int count = 0, string? database = null) => new()
    {
        Name = application.Name,
        ServiceName = application.ServiceName,
        Database = database,
        Status = status,
        ErrorCount = count,
    };

    private RawSqlErrorEvent? TryMap(JsonElement element, SigNozApplication application)
    {
        if (!TryReadTimestamp(element, out var timestamp)
            || !TryGetString(element, out var message, "exception.message", "exception_message", "message", "body")
            || message is null
            || !IsSqlRelated(element, message))
        {
            return null;
        }

        var exceptionType = ReadString(element, "exception.type", "exception_type", "error.type");
        var errorNumber = ReadInt(element, "sql_error_number", "db.sql.error_code", "error.number");
        var timeout = IsExplicitCommandTimeout(element, message, exceptionType, errorNumber);

        return new RawSqlErrorEvent
        {
            Timestamp = timestamp,
            Server = ReadString(element, "server.address", "server", "db.server") ,
            Database = ReadString(element, "db.namespace", "database", "db.name"),
            Application = application.Name,
            Environment = ReadString(element, "deployment.environment", "environment"),
            Host = ReadString(element, "host.name", "host"),
            SessionIdentifier = ReadString(element, "session.id", "session_identifier", "session_id"),
            TraceId = ReadString(element, "trace_id", "traceId"),
            CorrelationId = ReadString(element, "correlation_id", "correlationId"),
            RequestEndpoint = ReadString(element, "http.route", "http.route.name", "endpoint"),
            StoredProcedure = ReadString(element, "stored_procedure", "db.operation.name"),
            QueryHash = ReadString(element, "query_hash", "db.query.hash"),
            SqlErrorNumber = errorNumber,
            SqlErrorState = ReadInt(element, "sql_error_state", "db.sql.error_state"),
            SqlErrorClass = ReadInt(element, "sql_error_class", "db.sql.error_class"),
            Message = Sanitize(message),
            ExceptionType = Sanitize(exceptionType),
            CommandTimeoutSeconds = ReadDouble(element, "command_timeout_seconds", "db.command.timeout"),
            DurationMs = ReadDurationMs(element),
            ApplicationTimeoutReported = timeout,
            SourceName = Name,
            EventName = ReadString(element, "name", "span.name"),
            Parameters = _options.CaptureParameterValues ? ReadParameters(element) : EmptyParameters,
        };
    }

    private static bool IsForConfiguredService(JsonElement element, string serviceName) =>
        string.Equals(ReadString(element, "service.name", "service"), serviceName, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static readonly IReadOnlyDictionary<string, string?> EmptyParameters =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> ReadParameters(JsonElement element)
    {
        foreach (var name in new[] { "parameters", "db.parameters", "sql.parameters", "db.statement.parameters" })
        {
            if (TryGetPropertyRecursive(element, name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    result[property.Name] = MaskParameter(property.Name, property.Value.ToString());
                }
                return result;
            }
        }

        return EmptyParameters;
    }

    private static string? MaskParameter(string name, string? value)
    {
        if (SensitiveName.IsMatch(name) || SensitiveValue.IsMatch(value ?? string.Empty))
        {
            return "[REDACTED]";
        }

        return Sanitize(value);
    }

    private static readonly System.Text.RegularExpressions.Regex SensitiveName =
        new("password|passwd|secret|token|api.?key|credential|connection.?string", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SensitiveValue =
        new("(bearer\\s+|eyJ[a-zA-Z0-9_-]+\\.|server=.*(password|pwd)=|data source=.*password=)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsSqlRelated(JsonElement element, string message)
    {
        var dbSystem = ReadString(element, "db.system");
        var exceptionType = ReadString(element, "exception.type", "exception_type", "error.type");
        var sqlServerSystem = dbSystem is not null &&
            (dbSystem.Equals("mssql", StringComparison.OrdinalIgnoreCase)
             || dbSystem.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)
             || dbSystem.Equals("microsoft.sqlserver", StringComparison.OrdinalIgnoreCase));
        var sqlException = exceptionType?.Contains("SqlException", StringComparison.OrdinalIgnoreCase) == true
            || exceptionType?.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) == true;
        var sqlErrorNumber = ReadInt(element, "sql_error_number", "db.sql.error_code", "error.number") is not null;
        var recognizedSqlFailure = message.Contains("sql", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("command timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection to the server", StringComparison.OrdinalIgnoreCase);
        return sqlServerSystem || sqlException || sqlErrorNumber || recognizedSqlFailure;
    }

    private static bool IsExplicitCommandTimeout(
        JsonElement element,
        string message,
        string? exceptionType,
        int? errorNumber) =>
        ReadBool(element, "application_timeout_reported", "command_timeout_reported", "timeout")
        || errorNumber == SqlErrorNumbers.ClientCommandTimeout
        || ((exceptionType?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
             || message.Contains("timeout expired", StringComparison.OrdinalIgnoreCase)
             || message.Contains("command timeout", StringComparison.OrdinalIgnoreCase))
            && (exceptionType?.Contains("sql", StringComparison.OrdinalIgnoreCase) == true
                || message.Contains("sql", StringComparison.OrdinalIgnoreCase)
                || message.Contains("database", StringComparison.OrdinalIgnoreCase)));

    private static string BuildEventKey(RawSqlErrorEvent error)
    {
        if (!string.IsNullOrWhiteSpace(error.TraceId))
        {
            return $"trace:{error.TraceId}";
        }

        if (!string.IsNullOrWhiteSpace(error.CorrelationId))
        {
            return $"correlation:{error.CorrelationId}";
        }

        return string.Join('|',
            error.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            error.Database,
            error.StoredProcedure,
            error.QueryHash,
            error.SqlErrorNumber,
            error.Message);
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjects(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string? ReadString(JsonElement element, params string[] names) =>
        TryGetString(element, out var value, names) ? value : null;

    private static bool TryGetString(JsonElement element, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyRecursive(element, name, out var candidate))
            {
                value = candidate.ValueKind == JsonValueKind.String
                    ? candidate.GetString()
                    : candidate.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static int? ReadInt(JsonElement element, params string[] names) =>
        int.TryParse(ReadString(element, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ReadDouble(JsonElement element, params string[] names) =>
        double.TryParse(ReadString(element, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ReadDurationMs(JsonElement element)
    {
        var milliseconds = ReadDouble(element, "duration_ms", "duration.milliseconds");
        if (milliseconds is not null)
        {
            return milliseconds;
        }

        var nanos = ReadDouble(element, "duration_nano", "duration_ns", "duration.nanoseconds");
        return nanos / 1_000_000d;
    }

    private static bool ReadBool(JsonElement element, params string[] names) =>
        bool.TryParse(ReadString(element, names), out var value) && value;

    private static bool TryReadTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        var value = ReadString(element, "timestamp", "timestamp_ms", "time_unix_nano", "start_time");
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            timestamp = number > 1e15
                ? DateTimeOffset.UnixEpoch.AddTicks((long)(number / 100d))
                : number > 1e12
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)number)
                    : DateTimeOffset.FromUnixTimeSeconds((long)number);
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool TryGetPropertyRecursive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryGetPropertyRecursive(property.Value, name, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryGetPropertyRecursive(item, name, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = LlmSanitizer.SanitizeText(value);
        return sanitized[..Math.Min(sanitized.Length, MaxMessageLength)];
    }
}
