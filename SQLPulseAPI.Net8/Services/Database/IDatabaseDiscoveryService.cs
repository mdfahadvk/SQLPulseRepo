using SQLPulseAPI.Net8.Infrastructure;

namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>A user database that is a valid monitoring target.</summary>
/// <param name="Name">Database name.</param>
/// <param name="Status">SQL Server <c>state_desc</c>, always <c>ONLINE</c> for discovered databases.</param>
public sealed record DatabaseInfo(string Name, string Status);

/// <summary>Result of a discovery attempt, carrying a sanitized failure reason.</summary>
/// <param name="Outcome">Sanitized status and message.</param>
/// <param name="Databases">Discovered databases; empty when the attempt failed.</param>
public sealed record DatabaseDiscoveryResult(SqlOperationOutcome Outcome, IReadOnlyList<DatabaseInfo> Databases)
{
    /// <summary>True when discovery succeeded.</summary>
    public bool IsSuccess => Outcome.IsSuccess;
}

/// <summary>
/// Discovers the databases the monitoring account may analyse and validates
/// user-supplied database names against that set.
/// Ported from <c>app/database/discovery.py</c>.
/// </summary>
public interface IDatabaseDiscoveryService
{
    /// <summary>
    /// Returns online, accessible, non-system databases.
    /// </summary>
    /// <param name="useCache">When false, always queries SQL Server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DatabaseDiscoveryResult> DiscoverDatabasesAsync(
        bool useCache = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a user-supplied database name.
    /// Returns <see langword="null"/> when valid, otherwise a caller-safe error message.
    /// </summary>
    Task<string?> ValidateTargetDatabaseAsync(string? database, CancellationToken cancellationToken = default);

    /// <summary>Clears the cached database list.</summary>
    void InvalidateCache();
}
