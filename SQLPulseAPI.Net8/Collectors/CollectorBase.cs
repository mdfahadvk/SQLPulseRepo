using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>Scope at which a collector operates. Python: <c>CollectorScope</c>.</summary>
public enum CollectorScope
{
    /// <summary>Instance-wide only.</summary>
    Server,

    /// <summary>Single database only.</summary>
    Database,

    /// <summary>Works at either scope.</summary>
    Both,
}

/// <summary>
/// Shared behaviour for every performance collector.
/// Ported from <c>BaseCollector</c> in <c>app/collectors/base.py</c>.
/// </summary>
/// <remarks>
/// A collector never throws. Every failure is captured in
/// <see cref="CollectorMetadata"/> so that one unavailable or permission-denied
/// collector degrades a single section of the snapshot instead of the whole thing.
/// </remarks>
public abstract class CollectorBase(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger logger)
{
    /// <summary>
    /// Phrases that indicate a permission failure in a driver-agnostic way.
    /// Mirrors <c>BaseCollector._is_permission_error</c>.
    /// </summary>
    private static readonly string[] PermissionPhrases =
    [
        "permission",
        "view server state",
        "view database state",
        "denied",
        "not have permission",
    ];

    private readonly SqlServerOptions _sqlServer = sqlServerOptions.Value;

    /// <summary>Stable collector name, surfaced in <see cref="CollectorMetadata.CollectorName"/>.</summary>
    public abstract string Name { get; }

    /// <summary>Scope this collector supports. Defaults to <see cref="CollectorScope.Both"/>.</summary>
    public virtual CollectorScope Scope => CollectorScope.Both;

    /// <summary>Logger for the concrete collector.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>Configured server host. Safe to log and to return to callers.</summary>
    protected string ConfiguredServerHost => _sqlServer.Server;

    /// <summary>Runs a read-only query, optionally against a specific database.</summary>
    protected Task<IReadOnlyList<SqlRow>> ExecuteQueryAsync(
        string query,
        string? database,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteReadQueryAsync(query, database, parameters, cancellationToken);

    /// <summary>Builds metadata describing a successful collection.</summary>
    protected CollectorMetadata BuildMetadata(string? database) => new()
    {
        CollectorName = Name,
        ServerHost = _sqlServer.Server,
        Database = string.IsNullOrWhiteSpace(database) ? _sqlServer.DefaultDatabase : database,
        CollectedAt = DateTimeOffset.UtcNow,
        Success = true,
    };

    /// <summary>
    /// Builds metadata describing a failed collection, logging the original exception
    /// and returning only a sanitized message.
    /// </summary>
    /// <remarks>
    /// SQL Server error text routinely echoes login names, database names and statement
    /// fragments. The Python implementation copies <c>str(exc)</c> straight into
    /// <c>error_message</c>; here the raw exception is logged and the response carries one
    /// of the fixed messages from <see cref="SqlErrorSanitizer"/> instead.
    /// </remarks>
    protected CollectorMetadata BuildFailureMetadata(string? database, Exception exception)
    {
        var permissionIssue = IsPermissionError(exception);

        Logger.LogError(
            exception,
            "{Collector} failed (permission_issue={PermissionIssue}, database={Database})",
            Name,
            permissionIssue,
            database ?? _sqlServer.DefaultDatabase);

        var message = permissionIssue
            ? SqlErrorSanitizer.PermissionDeniedMessage
            : SqlErrorSanitizer.Sanitize(exception).Message;

        return new CollectorMetadata
        {
            CollectorName = Name,
            ServerHost = _sqlServer.Server,
            Database = string.IsNullOrWhiteSpace(database) ? _sqlServer.DefaultDatabase : database,
            CollectedAt = DateTimeOffset.UtcNow,
            Success = false,
            ErrorMessage = message,
            PermissionIssue = permissionIssue,
        };
    }

    /// <summary>Returns true when the exception indicates the monitoring login lacks a permission.</summary>
    protected static bool IsPermissionError(Exception exception)
    {
        if (exception is SqlException sqlException && SqlErrorSanitizer.IsPermissionError(sqlException))
        {
            return true;
        }

        var message = exception.Message;
        foreach (var phrase in PermissionPhrases)
        {
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // --- Row mapping helpers -------------------------------------------------
    // Values arrive boxed and their SQL types vary (int, bigint, numeric, float),
    // so every read goes through Convert with the invariant culture.

    /// <summary>Reads a column as <see cref="int"/>, returning 0 when absent or NULL.</summary>
    protected static int ReadInt32(SqlRow row, string column) =>
        row[column] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    /// <summary>Reads a column as a nullable <see cref="int"/>.</summary>
    protected static int? ReadNullableInt32(SqlRow row, string column) =>
        row[column] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Reads a column as <see cref="long"/>, returning 0 when absent or NULL.</summary>
    protected static long ReadInt64(SqlRow row, string column) =>
        row[column] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0L;

    /// <summary>Reads a column as <see cref="double"/>, returning 0 when absent or NULL.</summary>
    protected static double ReadDouble(SqlRow row, string column) =>
        row[column] is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0d;

    /// <summary>Reads a column as a nullable string.</summary>
    protected static string? ReadNullableString(SqlRow row, string column) => row.GetString(column);

    /// <summary>Reads a column as a string, returning an empty string when absent or NULL.</summary>
    protected static string ReadString(SqlRow row, string column) => row.GetString(column) ?? string.Empty;

    /// <summary>Reads a column as <see cref="bool"/>, treating a non-zero number as true.</summary>
    protected static bool ReadBoolean(SqlRow row, string column) =>
        row[column] is { } value && Convert.ToBoolean(Convert.ToInt64(value, CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads a column as a nullable <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// <c>datetime</c> columns carry no offset, so an unqualified value is treated as UTC
    /// to keep the JSON contract on a single, unambiguous representation.
    /// </remarks>
    protected static DateTimeOffset? ReadNullableTimestamp(SqlRow row, string column) => row[column] switch
    {
        null => null,
        DateTimeOffset offset => offset,
        DateTime { Kind: DateTimeKind.Unspecified } naive => new DateTimeOffset(naive, TimeSpan.Zero),
        DateTime dateTime => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
        var other => new DateTimeOffset(
            DateTime.SpecifyKind(Convert.ToDateTime(other, CultureInfo.InvariantCulture), DateTimeKind.Utc)),
    };
}
