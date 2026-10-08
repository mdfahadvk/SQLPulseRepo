using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;

namespace SQLPulseAPI.Net8.Repositories;

/// <summary>
/// SQLite-backed <see cref="IHistoryRepository"/>.
/// Ported from <c>SQLiteHistoryRepository</c> in <c>app/storage/sqlite_repository.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// This database belongs to SQLPulse and is completely separate from the monitored SQL
/// Server, which is only ever read.
/// </para>
/// <para>
/// Every method opens a short-lived connection and closes it before returning, so a write
/// lock is never held across a slow external call. Concurrency is handled by
/// write-ahead logging, which lets readers continue while a write is in progress, plus a
/// busy timeout so a concurrent writer waits instead of failing immediately. ASP.NET Core
/// serves requests in parallel, so this matters far more here than it did in the
/// single-threaded Python original.
/// </para>
/// </remarks>
public sealed class SqliteHistoryRepository : IHistoryRepository
{
    /// <summary>How long a blocked writer waits before giving up.</summary>
    private const int BusyTimeoutMs = 30_000;

    /// <summary>Timestamps are stored as round-trippable ISO-8601 so ordering is lexical.</summary>
    private const string TimestampFormat = "O";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS snapshots (
            snapshot_id TEXT PRIMARY KEY,
            collected_at TEXT NOT NULL,
            server TEXT NOT NULL,
            scope TEXT NOT NULL,
            database_name TEXT
        );

        CREATE TABLE IF NOT EXISTS query_metrics (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id TEXT NOT NULL,
            collected_at TEXT NOT NULL,
            server TEXT NOT NULL,
            database_name TEXT NOT NULL,
            query_hash TEXT NOT NULL,
            sql_text_preview TEXT,
            execution_count INTEGER DEFAULT 0,
            total_cpu_time_ms INTEGER DEFAULT 0,
            avg_cpu_time_ms REAL DEFAULT 0.0,
            total_elapsed_time_ms INTEGER DEFAULT 0,
            avg_elapsed_time_ms REAL DEFAULT 0.0,
            total_logical_reads INTEGER DEFAULT 0,
            avg_logical_reads REAL DEFAULT 0.0,
            total_physical_reads INTEGER DEFAULT 0,
            avg_physical_reads REAL DEFAULT 0.0,
            total_logical_writes INTEGER DEFAULT 0,
            avg_logical_writes REAL DEFAULT 0.0,
            UNIQUE(snapshot_id, query_hash)
        );

        CREATE INDEX IF NOT EXISTS idx_qm_hash_db
            ON query_metrics(query_hash, database_name);
        CREATE INDEX IF NOT EXISTS idx_qm_collected
            ON query_metrics(collected_at);
        CREATE INDEX IF NOT EXISTS idx_snapshots_collected
            ON snapshots(collected_at);

        CREATE TABLE IF NOT EXISTS investigation_audits (
            investigation_id TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            question_sanitized TEXT NOT NULL,
            server TEXT NOT NULL,
            database_name TEXT,
            time_range_start TEXT,
            time_range_end TEXT,
            tools_called TEXT NOT NULL DEFAULT '[]',
            finding_references TEXT NOT NULL DEFAULT '[]',
            severity TEXT NOT NULL DEFAULT 'unknown',
            confidence TEXT NOT NULL DEFAULT 'low',
            answer TEXT NOT NULL DEFAULT '',
            human_review_required INTEGER NOT NULL DEFAULT 1,
            remediation_performed INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_audit_created
            ON investigation_audits(created_at);

        CREATE TABLE IF NOT EXISTS wait_totals (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id TEXT NOT NULL UNIQUE,
            collected_at TEXT NOT NULL,
            server TEXT NOT NULL,
            sqlserver_start_time TEXT,
            waiting_tasks_count INTEGER DEFAULT 0,
            wait_time_ms INTEGER DEFAULT 0,
            signal_wait_time_ms INTEGER DEFAULT 0,
            resource_wait_time_ms INTEGER DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_wait_totals_server_collected
            ON wait_totals(server, collected_at);
        """;

    private readonly string _connectionString;
    private readonly ILogger<SqliteHistoryRepository> _logger;

    public SqliteHistoryRepository(
        IOptions<HistoryOptions> options,
        ILogger<SqliteHistoryRepository> logger)
    {
        _logger = logger;
        DatabasePath = options.Value.DatabasePath;

        // Built through the builder so a path containing separators or quotes cannot
        // alter the rest of the connection string.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache, not shared. Shared cache serializes on table-level locks
            // that busy_timeout does not cover, so concurrent writers would fail
            // immediately with SQLITE_LOCKED instead of waiting. WAL plus a busy timeout
            // is what actually makes concurrent request handling safe here.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMs / 1000,
        }.ToString();
    }

    /// <summary>Path to the SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("SQLite history storage initialized at {Path}", DatabasePath);
    }

    /// <inheritdoc />
    public async Task SaveSnapshotAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // INSERT OR IGNORE: re-persisting a snapshot must not duplicate or fail.
        command.CommandText = """
            INSERT OR IGNORE INTO snapshots
                (snapshot_id, collected_at, server, scope, database_name)
            VALUES ($snapshot_id, $collected_at, $server, $scope, $database_name)
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$collected_at", Format(snapshot.CollectedAt));
        command.Parameters.AddWithValue("$server", snapshot.Server);
        command.Parameters.AddWithValue("$scope", snapshot.Scope);
        command.Parameters.AddWithValue("$database_name", (object?)snapshot.Database ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SnapshotExistsAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM snapshots WHERE snapshot_id = $snapshot_id";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc />
    public async Task SaveQueryMetricsAsync(
        IReadOnlyCollection<QueryMetricRecord> metrics,
        CancellationToken cancellationToken = default)
    {
        if (metrics.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;

        command.CommandText = """
            INSERT OR IGNORE INTO query_metrics
                (snapshot_id, collected_at, server, database_name, query_hash,
                 sql_text_preview, execution_count,
                 total_cpu_time_ms, avg_cpu_time_ms,
                 total_elapsed_time_ms, avg_elapsed_time_ms,
                 total_logical_reads, avg_logical_reads,
                 total_physical_reads, avg_physical_reads,
                 total_logical_writes, avg_logical_writes)
            VALUES ($snapshot_id, $collected_at, $server, $database_name, $query_hash,
                    $sql_text_preview, $execution_count,
                    $total_cpu_time_ms, $avg_cpu_time_ms,
                    $total_elapsed_time_ms, $avg_elapsed_time_ms,
                    $total_logical_reads, $avg_logical_reads,
                    $total_physical_reads, $avg_physical_reads,
                    $total_logical_writes, $avg_logical_writes)
            """;

        // Parameters are declared once and re-bound per row so the statement is prepared
        // a single time for the whole batch.
        var snapshotId = command.Parameters.Add("$snapshot_id", SqliteType.Text);
        var collectedAt = command.Parameters.Add("$collected_at", SqliteType.Text);
        var server = command.Parameters.Add("$server", SqliteType.Text);
        var database = command.Parameters.Add("$database_name", SqliteType.Text);
        var queryHash = command.Parameters.Add("$query_hash", SqliteType.Text);
        var preview = command.Parameters.Add("$sql_text_preview", SqliteType.Text);
        var executionCount = command.Parameters.Add("$execution_count", SqliteType.Integer);
        var totalCpu = command.Parameters.Add("$total_cpu_time_ms", SqliteType.Integer);
        var avgCpu = command.Parameters.Add("$avg_cpu_time_ms", SqliteType.Real);
        var totalElapsed = command.Parameters.Add("$total_elapsed_time_ms", SqliteType.Integer);
        var avgElapsed = command.Parameters.Add("$avg_elapsed_time_ms", SqliteType.Real);
        var totalReads = command.Parameters.Add("$total_logical_reads", SqliteType.Integer);
        var avgReads = command.Parameters.Add("$avg_logical_reads", SqliteType.Real);
        var totalPhysical = command.Parameters.Add("$total_physical_reads", SqliteType.Integer);
        var avgPhysical = command.Parameters.Add("$avg_physical_reads", SqliteType.Real);
        var totalWrites = command.Parameters.Add("$total_logical_writes", SqliteType.Integer);
        var avgWrites = command.Parameters.Add("$avg_logical_writes", SqliteType.Real);

        foreach (var metric in metrics)
        {
            snapshotId.Value = metric.SnapshotId;
            collectedAt.Value = Format(metric.CollectedAt);
            server.Value = metric.Server;
            database.Value = metric.Database;
            queryHash.Value = metric.QueryHash;
            preview.Value = (object?)metric.SqlTextPreview ?? DBNull.Value;
            executionCount.Value = metric.ExecutionCount;
            totalCpu.Value = metric.TotalCpuTimeMs;
            avgCpu.Value = metric.AvgCpuTimeMs;
            totalElapsed.Value = metric.TotalElapsedTimeMs;
            avgElapsed.Value = metric.AvgElapsedTimeMs;
            totalReads.Value = metric.TotalLogicalReads;
            avgReads.Value = metric.AvgLogicalReads;
            totalPhysical.Value = metric.TotalPhysicalReads;
            avgPhysical.Value = metric.AvgPhysicalReads;
            totalWrites.Value = metric.TotalLogicalWrites;
            avgWrites.Value = metric.AvgLogicalWrites;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveWaitTotalsAsync(WaitTotalsRecord totals, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO wait_totals
                (snapshot_id, collected_at, server, sqlserver_start_time,
                 waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
                 resource_wait_time_ms)
            VALUES ($snapshot_id, $collected_at, $server, $sqlserver_start_time,
                    $waiting_tasks_count, $wait_time_ms, $signal_wait_time_ms,
                    $resource_wait_time_ms)
            """;
        command.Parameters.AddWithValue("$snapshot_id", totals.SnapshotId);
        command.Parameters.AddWithValue("$collected_at", Format(totals.CollectedAt));
        command.Parameters.AddWithValue("$server", totals.Server);
        command.Parameters.AddWithValue(
            "$sqlserver_start_time",
            totals.SqlServerStartTime is { } start ? Format(start) : DBNull.Value);
        command.Parameters.AddWithValue("$waiting_tasks_count", totals.WaitingTasksCount);
        command.Parameters.AddWithValue("$wait_time_ms", totals.WaitTimeMs);
        command.Parameters.AddWithValue("$signal_wait_time_ms", totals.SignalWaitTimeMs);
        command.Parameters.AddWithValue("$resource_wait_time_ms", totals.ResourceWaitTimeMs);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WaitTotalsRecord?> GetLatestWaitTotalsAsync(
        string server,
        DateTimeOffset? before = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = before is null
            ? """
              SELECT * FROM wait_totals
              WHERE server = $server
              ORDER BY collected_at DESC LIMIT 1
              """
            : """
              SELECT * FROM wait_totals
              WHERE server = $server AND collected_at < $before
              ORDER BY collected_at DESC LIMIT 1
              """;
        command.Parameters.AddWithValue("$server", server);
        if (before is { } cutoff)
        {
            command.Parameters.AddWithValue("$before", Format(cutoff));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WaitTotalsRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            SnapshotId = reader.GetString(reader.GetOrdinal("snapshot_id")),
            CollectedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("collected_at"))),
            Server = reader.GetString(reader.GetOrdinal("server")),
            SqlServerStartTime = ReadNullableTimestamp(reader, "sqlserver_start_time"),
            WaitingTasksCount = reader.GetInt64(reader.GetOrdinal("waiting_tasks_count")),
            WaitTimeMs = reader.GetInt64(reader.GetOrdinal("wait_time_ms")),
            SignalWaitTimeMs = reader.GetInt64(reader.GetOrdinal("signal_wait_time_ms")),
            ResourceWaitTimeMs = reader.GetInt64(reader.GetOrdinal("resource_wait_time_ms")),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryMetricRecord>> GetQueryHistoryAsync(
        string queryHash,
        string database,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT * FROM query_metrics
            WHERE query_hash = $query_hash AND database_name = $database_name
            ORDER BY collected_at DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query_hash", queryHash);
        command.Parameters.AddWithValue("$database_name", database);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadMetricsAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryMetricRecord>> GetQuerySamplesAsync(
        string queryHash,
        string database,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Ascending: a baseline is computed over the window as a time series.
        command.CommandText = """
            SELECT * FROM query_metrics
            WHERE query_hash = $query_hash
              AND database_name = $database_name
              AND collected_at >= $since
            ORDER BY collected_at ASC
            """;
        command.Parameters.AddWithValue("$query_hash", queryHash);
        command.Parameters.AddWithValue("$database_name", database);
        command.Parameters.AddWithValue("$since", Format(since));

        return await ReadMetricsAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveInvestigationAuditAsync(
        InvestigationAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO investigation_audits
                (investigation_id, created_at, question_sanitized, server,
                 database_name, time_range_start, time_range_end,
                 tools_called, finding_references,
                 severity, confidence, answer,
                 human_review_required, remediation_performed)
            VALUES ($investigation_id, $created_at, $question_sanitized, $server,
                    $database_name, $time_range_start, $time_range_end,
                    $tools_called, $finding_references,
                    $severity, $confidence, $answer,
                    $human_review_required, $remediation_performed)
            """;
        command.Parameters.AddWithValue("$investigation_id", record.InvestigationId);
        command.Parameters.AddWithValue("$created_at", Format(record.CreatedAt));
        command.Parameters.AddWithValue("$question_sanitized", record.QuestionSanitized);
        command.Parameters.AddWithValue("$server", record.Server);
        command.Parameters.AddWithValue("$database_name", (object?)record.Database ?? DBNull.Value);
        command.Parameters.AddWithValue("$time_range_start", (object?)record.TimeRangeStart ?? DBNull.Value);
        command.Parameters.AddWithValue("$time_range_end", (object?)record.TimeRangeEnd ?? DBNull.Value);
        command.Parameters.AddWithValue("$tools_called", JsonSerializer.Serialize(record.ToolsCalled));
        command.Parameters.AddWithValue("$finding_references", JsonSerializer.Serialize(record.FindingReferences));
        command.Parameters.AddWithValue("$severity", record.Severity);
        command.Parameters.AddWithValue("$confidence", record.Confidence);
        command.Parameters.AddWithValue("$answer", record.Answer);
        command.Parameters.AddWithValue("$human_review_required", record.HumanReviewRequired ? 1 : 0);
        command.Parameters.AddWithValue("$remediation_performed", record.RemediationPerformed ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InvestigationAuditRecord?> GetInvestigationAuditAsync(
        string investigationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM investigation_audits WHERE investigation_id = $id";
        command.Parameters.AddWithValue("$id", investigationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAudit(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvestigationAuditRecord>> ListInvestigationAuditsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM investigation_audits ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<InvestigationAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapAudit(reader));
        }

        return records;
    }

    /// <inheritdoc />
    public async Task<int> RunRetentionCleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = Format(DateTimeOffset.UtcNow.AddDays(-retentionDays));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int deleted;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM query_metrics WHERE collected_at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Snapshot headers and wait totals age out with the metrics they describe.
        foreach (var table in new[] { "snapshots", "wait_totals" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE collected_at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Retention cleanup: deleted {Count} metric record(s) older than {Cutoff}", deleted, cutoff);

        return deleted;
    }

    /// <summary>
    /// Opens a connection configured for concurrent access.
    /// </summary>
    /// <remarks>
    /// WAL lets readers proceed while a write is in flight, and the busy timeout makes a
    /// blocked writer wait rather than fail. Both pragmas are set per connection because
    /// the pool hands back connections whose session state was reset.
    /// </remarks>
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<QueryMetricRecord>> ReadMetricsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<QueryMetricRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new QueryMetricRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SnapshotId = reader.GetString(reader.GetOrdinal("snapshot_id")),
                CollectedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("collected_at"))),
                Server = reader.GetString(reader.GetOrdinal("server")),
                Database = reader.GetString(reader.GetOrdinal("database_name")),
                QueryHash = reader.GetString(reader.GetOrdinal("query_hash")),
                SqlTextPreview = ReadNullableString(reader, "sql_text_preview"),
                ExecutionCount = reader.GetInt64(reader.GetOrdinal("execution_count")),
                TotalCpuTimeMs = reader.GetInt64(reader.GetOrdinal("total_cpu_time_ms")),
                AvgCpuTimeMs = reader.GetDouble(reader.GetOrdinal("avg_cpu_time_ms")),
                TotalElapsedTimeMs = reader.GetInt64(reader.GetOrdinal("total_elapsed_time_ms")),
                AvgElapsedTimeMs = reader.GetDouble(reader.GetOrdinal("avg_elapsed_time_ms")),
                TotalLogicalReads = reader.GetInt64(reader.GetOrdinal("total_logical_reads")),
                AvgLogicalReads = reader.GetDouble(reader.GetOrdinal("avg_logical_reads")),
                TotalPhysicalReads = reader.GetInt64(reader.GetOrdinal("total_physical_reads")),
                AvgPhysicalReads = reader.GetDouble(reader.GetOrdinal("avg_physical_reads")),
                TotalLogicalWrites = reader.GetInt64(reader.GetOrdinal("total_logical_writes")),
                AvgLogicalWrites = reader.GetDouble(reader.GetOrdinal("avg_logical_writes")),
            });
        }

        return records;
    }

    private static InvestigationAuditRecord MapAudit(IDataRecord reader)
    {
        var toolsJson = ReadNullableString(reader, "tools_called");
        var referencesJson = ReadNullableString(reader, "finding_references");

        return new InvestigationAuditRecord
        {
            InvestigationId = reader.GetString(reader.GetOrdinal("investigation_id")),
            CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
            QuestionSanitized = reader.GetString(reader.GetOrdinal("question_sanitized")),
            Server = reader.GetString(reader.GetOrdinal("server")),
            Database = ReadNullableString(reader, "database_name"),
            TimeRangeStart = ReadNullableString(reader, "time_range_start"),
            TimeRangeEnd = ReadNullableString(reader, "time_range_end"),
            ToolsCalled = string.IsNullOrEmpty(toolsJson)
                ? []
                : JsonSerializer.Deserialize<List<AuditToolCall>>(toolsJson) ?? [],
            FindingReferences = string.IsNullOrEmpty(referencesJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(referencesJson) ?? [],
            Severity = reader.GetString(reader.GetOrdinal("severity")),
            Confidence = reader.GetString(reader.GetOrdinal("confidence")),
            Answer = reader.GetString(reader.GetOrdinal("answer")),
            HumanReviewRequired = reader.GetInt64(reader.GetOrdinal("human_review_required")) != 0,
            RemediationPerformed = reader.GetInt64(reader.GetOrdinal("remediation_performed")) != 0,
        };
    }

    private static string? ReadNullableString(IDataRecord reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableTimestamp(IDataRecord reader, string column)
    {
        var value = ReadNullableString(reader, column);
        return value is null ? null : ParseTimestamp(value);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
