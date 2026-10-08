using System.Data;
using Microsoft.Data.SqlClient;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>
/// Runs read-only queries through <see cref="IDatabaseConnectionFactory"/>.
/// </summary>
public sealed class SqlQueryExecutor(
    IDatabaseConnectionFactory connectionFactory,
    ILogger<SqlQueryExecutor> logger) : ISqlQueryExecutor
{
    public string ServerHost => connectionFactory.ServerHost;

    public bool IsConfigured => connectionFactory.IsConfigured;

    public async Task<IReadOnlyList<SqlRow>> ExecuteReadQueryAsync(
        string sql,
        string? database = null,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Validate before opening a connection, so a rejected statement never
        // reaches the server at all.
        SqlSafety.EnsureReadOnly(sql);

        await using var connection = connectionFactory.CreateConnection(database);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = connectionFactory.CommandTimeoutSeconds;

        if (parameters is not null)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", parameters[i] ?? DBNull.Value);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<SqlRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(new SqlRow(values));
        }

        logger.LogDebug("Read query returned {RowCount} rows", rows.Count);
        return rows;
    }

    public async Task<SqlOperationOutcome> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!connectionFactory.IsConfigured)
        {
            return new SqlOperationOutcome(SqlOperationStatus.Failed, SqlErrorSanitizer.NotConfiguredMessage);
        }

        try
        {
            var rows = await ExecuteReadQueryAsync(
                SqlQueries.ConnectivityCheck,
                cancellationToken: cancellationToken);

            var connected = rows.Count > 0 && rows[0].GetValue<int>("connected") == 1;

            return connected
                ? SqlOperationOutcome.Success()
                : new SqlOperationOutcome(SqlOperationStatus.Failed, SqlErrorSanitizer.GenericFailureMessage);
        }
        catch (Exception ex)
        {
            // Logged with the full exception here; only the sanitized outcome escapes.
            logger.LogError(ex, "SQL Server connection test failed for {Server}", connectionFactory.ServerHost);
            return SqlErrorSanitizer.Sanitize(ex);
        }
    }
}
