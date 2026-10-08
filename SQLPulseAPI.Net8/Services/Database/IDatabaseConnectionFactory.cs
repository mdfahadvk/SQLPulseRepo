using Microsoft.Data.SqlClient;

namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>
/// Creates read-only SQL Server connections for collectors and services.
/// </summary>
public interface IDatabaseConnectionFactory
{
    /// <summary>True when server, username and password are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Configured server host. Safe to log.</summary>
    string ServerHost { get; }

    /// <summary>Database used when no explicit database is requested. Safe to log.</summary>
    string DefaultDatabase { get; }

    /// <summary>Command timeout in seconds applied to every query.</summary>
    int CommandTimeoutSeconds { get; }

    /// <summary>
    /// Creates a closed connection to the requested database, or to the default
    /// database when <paramref name="database"/> is <see langword="null"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">SQL Server is not configured.</exception>
    /// <exception cref="ArgumentException">The database name is not a valid identifier.</exception>
    SqlConnection CreateConnection(string? database = null);
}
