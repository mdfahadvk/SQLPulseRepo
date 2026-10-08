using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>
/// Builds SQL Server connections from <see cref="SqlServerOptions"/>.
/// Credentials and complete connection strings are never logged.
/// </summary>
public sealed class SqlServerConnectionFactory(
    IOptions<SqlServerOptions> options,
    ILogger<SqlServerConnectionFactory> logger) : IDatabaseConnectionFactory
{
    private readonly SqlServerOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string ServerHost => _options.Server;

    public string DefaultDatabase => _options.DefaultDatabase;

    public int CommandTimeoutSeconds => _options.CommandTimeoutSeconds;

    public SqlConnection CreateConnection(string? database = null)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "SQL Server is not configured. Set SqlServer:Server, SqlServer:Username and SqlServer:Password.");
        }

        var target = string.IsNullOrWhiteSpace(database) ? _options.DefaultDatabase : database;

        if (!DatabaseNames.IsValid(target))
        {
            throw new ArgumentException("Invalid database name.", nameof(database));
        }

        // Credentials are supplied through the builder rather than string
        // concatenation, so a malformed value cannot inject connection options.
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.Server,
            InitialCatalog = target,
            UserID = _options.Username,
            Password = _options.Password,
            Encrypt = _options.Encrypt,
            TrustServerCertificate = _options.TrustServerCertificate,
            ConnectTimeout = _options.ConnectionTimeoutSeconds,
            ApplicationName = _options.ApplicationName,
            PersistSecurityInfo = false,
        };

        logger.LogDebug("Creating SQL connection to {Server}/{Database}", _options.Server, target);

        return new SqlConnection(builder.ConnectionString);
    }
}
