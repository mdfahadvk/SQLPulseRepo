using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class DatabaseConnectionTests
{
    private const string TestPassword = "not-a-real-password";

    private static SqlServerConnectionFactory CreateFactory(SqlServerOptions options) =>
        new(Options.Create(options), NullLogger<SqlServerConnectionFactory>.Instance);

    private static SqlServerOptions ConfiguredOptions() => new()
    {
        Server = "sql01",
        Username = "sqlpulse_reader",
        Password = TestPassword,
        DefaultDatabase = "master",
    };

    [Fact]
    public void Creating_a_connection_without_configuration_fails()
    {
        var factory = CreateFactory(new SqlServerOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());

        Assert.DoesNotContain(TestPassword, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("master'; DROP DATABASE Sales; --")]
    [InlineData("Sales];[master")]
    [InlineData("Sales DB")]
    [InlineData("Sales.dbo")]
    [InlineData("Sales;")]
    public void Invalid_database_names_are_rejected(string database)
    {
        var factory = CreateFactory(ConfiguredOptions());

        Assert.Throws<ArgumentException>(() => factory.CreateConnection(database));
    }

    [Fact]
    public void A_blank_database_falls_back_to_the_default_database()
    {
        var factory = CreateFactory(ConfiguredOptions());

        using var connection = factory.CreateConnection("   ");

        Assert.Equal("master", connection.Database);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("AdventureWorks")]
    [InlineData("Sales_Reporting")]
    [InlineData("Sales-Reporting")]
    public void Valid_database_names_are_accepted(string database)
    {
        var factory = CreateFactory(ConfiguredOptions());

        using var connection = factory.CreateConnection(database);

        Assert.Equal(database, connection.Database);
    }

    [Fact]
    public void Connection_settings_come_from_configuration()
    {
        var options = ConfiguredOptions();
        options.ConnectionTimeoutSeconds = 15;
        options.CommandTimeoutSeconds = 45;
        var factory = CreateFactory(options);

        using var connection = factory.CreateConnection();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("sql01", builder.DataSource);
        Assert.Equal("sqlpulse_reader", builder.UserID);
        Assert.Equal(15, builder.ConnectTimeout);
        Assert.True(builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal(45, factory.CommandTimeoutSeconds);
        Assert.Equal("sql01", factory.ServerHost);
        Assert.Equal("master", factory.DefaultDatabase);
        Assert.True(factory.IsConfigured);
    }

    [Fact]
    public void Connection_string_does_not_persist_security_info()
    {
        var factory = CreateFactory(ConfiguredOptions());

        using var connection = factory.CreateConnection();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);

        // With PersistSecurityInfo off, SQL Server drops the password from the
        // connection string once the connection is opened, so it cannot leak
        // through diagnostics.
        Assert.False(builder.PersistSecurityInfo);
        Assert.Equal("SQLPulse", builder.ApplicationName);
    }

    [Fact]
    public void System_databases_are_recognised()
    {
        Assert.True(DatabaseNames.IsSystemDatabase("tempdb"));
        Assert.True(DatabaseNames.IsSystemDatabase("MASTER"));
        Assert.False(DatabaseNames.IsSystemDatabase("AdventureWorks"));
    }

    [Fact]
    public void Sanitized_errors_never_carry_the_original_message()
    {
        var outcome = SqlErrorSanitizer.Sanitize(
            new Exception($"Server=sql01;User Id=sa;Password={TestPassword}"));

        Assert.Equal(SqlOperationStatus.Failed, outcome.Status);
        Assert.Equal(SqlErrorSanitizer.GenericFailureMessage, outcome.Message);
        Assert.DoesNotContain(TestPassword, outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sql01", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unconfigured_server_is_reported_distinctly()
    {
        var outcome = SqlErrorSanitizer.Sanitize(
            new InvalidOperationException("SQL Server is not configured. Set SqlServer:Server."));

        Assert.Equal(SqlOperationStatus.Failed, outcome.Status);
        Assert.Equal(SqlErrorSanitizer.NotConfiguredMessage, outcome.Message);
    }

    [Fact]
    public void A_read_only_violation_is_reported_as_a_failure()
    {
        var outcome = SqlErrorSanitizer.Sanitize(new ReadOnlyViolationException("Blocked ... DROP"));

        Assert.Equal(SqlOperationStatus.Failed, outcome.Status);
        Assert.Equal(SqlErrorSanitizer.GenericFailureMessage, outcome.Message);
    }

    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(262)]
    [InlineData(297)]
    [InlineData(300)]
    [InlineData(916)]
    [InlineData(15247)]
    [InlineData(21089)]
    public void Permission_error_numbers_are_classified(int number) =>
        Assert.True(SqlErrorSanitizer.IsPermissionErrorNumber(number));

    [Theory]
    [InlineData(4060)]  // Cannot open database requested by the login
    [InlineData(18456)] // Login failed
    [InlineData(-2)]    // Timeout
    public void Non_permission_error_numbers_are_not_classified_as_permission_issues(int number) =>
        Assert.False(SqlErrorSanitizer.IsPermissionErrorNumber(number));

    [Fact]
    public void Operation_statuses_serialize_to_the_expected_literals()
    {
        Assert.Equal(
            "\"success\"",
            System.Text.Json.JsonSerializer.Serialize(SqlOperationStatus.Success, SqlPulseJson.Options));
        Assert.Equal(
            "\"permission_denied\"",
            System.Text.Json.JsonSerializer.Serialize(SqlOperationStatus.PermissionDenied, SqlPulseJson.Options));
        Assert.Equal(
            "\"failed\"",
            System.Text.Json.JsonSerializer.Serialize(SqlOperationStatus.Failed, SqlPulseJson.Options));
        Assert.Equal(
            "\"not_applicable\"",
            System.Text.Json.JsonSerializer.Serialize(SqlOperationStatus.NotApplicable, SqlPulseJson.Options));
    }

    [Fact]
    public void A_permission_outcome_reports_itself_as_a_permission_issue()
    {
        var outcome = new SqlOperationOutcome(
            SqlOperationStatus.PermissionDenied,
            SqlErrorSanitizer.PermissionDeniedMessage);

        Assert.True(outcome.IsPermissionIssue);
        Assert.False(outcome.IsSuccess);
        Assert.True(SqlOperationOutcome.Success().IsSuccess);
    }
}
