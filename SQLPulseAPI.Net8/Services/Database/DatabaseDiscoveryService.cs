using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>
/// Discovers monitoring targets and validates database names against them.
/// </summary>
/// <remarks>
/// Discovery is the allow-list for every database-scoped endpoint. A name that did
/// not come back from <see cref="DiscoverDatabasesAsync"/> is never used, so a caller
/// cannot steer SQLPulse at a database the monitoring login has no access to.
/// </remarks>
public sealed class DatabaseDiscoveryService(
    ISqlQueryExecutor executor,
    ILogger<DatabaseDiscoveryService> logger) : IDatabaseDiscoveryService
{
    /// <summary>Returned when no database name was supplied.</summary>
    public const string DatabaseRequiredMessage = "Database name is required.";

    /// <summary>Returned when the name is not a safe identifier.</summary>
    public const string InvalidFormatMessage = "Invalid database name format.";

    /// <summary>Returned when discovery itself could not be performed.</summary>
    public const string DiscoveryUnavailableMessage =
        "Unable to verify the database because the database list could not be retrieved.";

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<DatabaseInfo>? _cachedDatabases;

    public async Task<DatabaseDiscoveryResult> DiscoverDatabasesAsync(
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        if (useCache)
        {
            var cached = Volatile.Read(ref _cachedDatabases);
            if (cached is not null)
            {
                return new DatabaseDiscoveryResult(SqlOperationOutcome.Success(), cached);
            }
        }

        if (!executor.IsConfigured)
        {
            return new DatabaseDiscoveryResult(
                new SqlOperationOutcome(SqlOperationStatus.Failed, SqlErrorSanitizer.NotConfiguredMessage),
                []);
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (useCache && _cachedDatabases is not null)
            {
                return new DatabaseDiscoveryResult(SqlOperationOutcome.Success(), _cachedDatabases);
            }

            var rows = await executor.ExecuteReadQueryAsync(
                SqlQueries.DiscoverDatabases,
                cancellationToken: cancellationToken);

            // The SQL already excludes system databases (database_id > 4), offline
            // databases (state_desc = 'ONLINE') and inaccessible ones (HAS_DBACCESS).
            // Re-filtering here keeps the guarantee even if the query is ever changed.
            var databases = rows
                .Select(row => new
                {
                    Name = row.GetString("name"),
                    Status = row.GetString("status") ?? string.Empty,
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .Where(row => !DatabaseNames.IsSystemDatabase(row.Name))
                .Where(row => string.Equals(row.Status, "ONLINE", StringComparison.OrdinalIgnoreCase))
                .Select(row => new DatabaseInfo(row.Name!, row.Status))
                .ToList();

            _cachedDatabases = databases;
            logger.LogInformation("Discovered {DatabaseCount} accessible databases", databases.Count);

            return new DatabaseDiscoveryResult(SqlOperationOutcome.Success(), databases);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to discover databases on {Server}", executor.ServerHost);
            return new DatabaseDiscoveryResult(SqlErrorSanitizer.Sanitize(ex), []);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<string?> ValidateTargetDatabaseAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return DatabaseRequiredMessage;
        }

        // Format is checked before discovery so a malformed name never reaches SQL Server.
        if (!DatabaseNames.IsValid(database))
        {
            return InvalidFormatMessage;
        }

        var result = await DiscoverDatabasesAsync(cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            return DiscoveryUnavailableMessage;
        }

        var accessible = result.Databases.Any(db => string.Equals(db.Name, database, StringComparison.Ordinal));

        return accessible ? null : $"Database '{database}' is not accessible or does not exist.";
    }

    public void InvalidateCache() => Volatile.Write(ref _cachedDatabases, null);
}
