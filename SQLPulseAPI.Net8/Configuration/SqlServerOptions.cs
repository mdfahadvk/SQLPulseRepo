using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// SQL Server connection settings (Python: <c>SQL_*</c> settings in <c>app/config.py</c>).
/// The ODBC driver name from the Python implementation has no .NET equivalent —
/// <c>Microsoft.Data.SqlClient</c> talks TDS directly.
/// </summary>
public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    /// <summary>SQL Server hostname or instance.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>SQL Server authentication username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>SQL Server authentication password. Supply via environment variable or user secrets — never commit.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Default database used for server-level queries.</summary>
    [Required]
    public string DefaultDatabase { get; set; } = "master";

    /// <summary>Encrypt the connection (Python: <c>sql_encrypt</c>).</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Trust the server certificate (Python: <c>sql_trust_server_certificate</c>).</summary>
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Connection timeout in seconds.</summary>
    [Range(1, 600)]
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>Command timeout in seconds applied to every query.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Application name reported to SQL Server, useful when tracing SQLPulse's own sessions.</summary>
    public string ApplicationName { get; set; } = "SQLPulse";

    /// <summary>True when server, username and password are all present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Server)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);
}
