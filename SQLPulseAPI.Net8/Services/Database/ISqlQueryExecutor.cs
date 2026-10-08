namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>One row returned by a read-only query, keyed by column name.</summary>
public sealed class SqlRow(IReadOnlyDictionary<string, object?> values)
{
    private readonly IReadOnlyDictionary<string, object?> _values = values;

    /// <summary>Column names in the order they were returned.</summary>
    public IEnumerable<string> Columns => _values.Keys;

    /// <summary>Raw column value, or <see langword="null"/> when absent or <c>NULL</c>.</summary>
    public object? this[string column] => _values.TryGetValue(column, out var value) ? value : null;

    /// <summary>Column value as a string, or <see langword="null"/>.</summary>
    public string? GetString(string column) => this[column]?.ToString();

    /// <summary>Column value converted to <typeparamref name="T"/>, or the default when absent or <c>NULL</c>.</summary>
    public T? GetValue<T>(string column)
    {
        var value = this[column];
        return value is null ? default : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Executes read-only queries against SQL Server.
/// Ported from <c>execute_read_query</c> in <c>app/database/connection.py</c>.
/// </summary>
/// <remarks>
/// Every query passes through <c>SqlSafety.EnsureReadOnly</c> before a connection is
/// even opened. This interface is the seam that lets services be unit tested without
/// a live SQL Server.
/// </remarks>
public interface ISqlQueryExecutor
{
    /// <summary>Configured server host. Safe to log and to return to callers.</summary>
    string ServerHost { get; }

    /// <summary>True when server, username and password are all configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Runs a read-only query and materialises the rows.
    /// </summary>
    /// <param name="sql">The query. Rejected if it is not read-only.</param>
    /// <param name="database">Target database, or <see langword="null"/> for the default database.</param>
    /// <param name="parameters">Positional parameter values for <c>@p0</c>, <c>@p1</c>, ….</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Utilities.ReadOnlyViolationException">The query is not read-only.</exception>
    Task<IReadOnlyList<SqlRow>> ExecuteReadQueryAsync(
        string sql,
        string? database = null,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies connectivity with a minimal read-only query.
    /// Never throws; failures are returned as a sanitized outcome.
    /// </summary>
    Task<Infrastructure.SqlOperationOutcome> TestConnectionAsync(CancellationToken cancellationToken = default);
}
