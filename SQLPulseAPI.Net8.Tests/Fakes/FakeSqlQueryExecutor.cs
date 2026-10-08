using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISqlQueryExecutor"/> so services and endpoints can be tested
/// without a SQL Server instance. No test in this project connects to a real server.
/// </summary>
public sealed class FakeSqlQueryExecutor : ISqlQueryExecutor
{
    private readonly List<string> _executedQueries = [];
    private readonly List<string?> _executedDatabases = [];
    private readonly List<IReadOnlyList<object?>?> _executedParameters = [];

    /// <summary>Rows returned by the next <see cref="ExecuteReadQueryAsync"/> call.</summary>
    public List<SqlRow> Rows { get; set; } = [];

    /// <summary>
    /// Selects rows based on the query text, taking precedence over <see cref="Rows"/>.
    /// Throwing from here simulates a single collector failing.
    /// </summary>
    public Func<string, IReadOnlyList<SqlRow>>? RowsForQuery { get; set; }

    /// <summary>When set, <see cref="ExecuteReadQueryAsync"/> throws this instead of returning rows.</summary>
    public Exception? ThrowOnExecute { get; set; }

    /// <summary>When set, <see cref="TestConnectionAsync"/> returns this outcome.</summary>
    public SqlOperationOutcome? ConnectionOutcome { get; set; }

    /// <summary>When set, <see cref="TestConnectionAsync"/> throws this.</summary>
    public Exception? ThrowOnTestConnection { get; set; }

    public string ServerHost { get; set; } = "sql01";

    public bool IsConfigured { get; set; } = true;

    /// <summary>Queries received, in order.</summary>
    public IReadOnlyList<string> ExecutedQueries => _executedQueries;

    /// <summary>Target database of each query received, in order.</summary>
    public IReadOnlyList<string?> ExecutedDatabases => _executedDatabases;

    /// <summary>Parameter values of each query received, in order.</summary>
    public IReadOnlyList<IReadOnlyList<object?>?> ExecutedParameters => _executedParameters;

    /// <summary>Number of queries received.</summary>
    public int ExecuteCount => _executedQueries.Count;

    /// <summary>Builds a database discovery row.</summary>
    public static SqlRow DatabaseRow(string name, string status = "ONLINE") =>
        new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = name,
            ["status"] = status,
        });

    /// <summary>Builds an arbitrary row from column/value pairs.</summary>
    public static SqlRow Row(params (string Column, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Column, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<SqlRow>> ExecuteReadQueryAsync(
        string sql,
        string? database = null,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // The real executor validates before connecting; the fake mirrors that so
        // tests cannot pass a mutating statement through unnoticed.
        SqlSafety.EnsureReadOnly(sql);
        _executedQueries.Add(sql);
        _executedDatabases.Add(database);
        _executedParameters.Add(parameters);

        if (ThrowOnExecute is not null)
        {
            throw ThrowOnExecute;
        }

        return Task.FromResult(RowsForQuery is not null
            ? RowsForQuery(sql)
            : (IReadOnlyList<SqlRow>)Rows);
    }

    public Task<SqlOperationOutcome> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnTestConnection is not null)
        {
            throw ThrowOnTestConnection;
        }

        return Task.FromResult(ConnectionOutcome ?? SqlOperationOutcome.Success());
    }
}
