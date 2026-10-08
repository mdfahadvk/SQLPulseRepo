using Microsoft.Extensions.Logging.Abstractions;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class DatabaseDiscoveryTests
{
    private static (DatabaseDiscoveryService Service, FakeSqlQueryExecutor Executor) CreateService(
        params SqlRow[] rows)
    {
        var executor = new FakeSqlQueryExecutor { Rows = [.. rows] };
        var service = new DatabaseDiscoveryService(executor, NullLogger<DatabaseDiscoveryService>.Instance);
        return (service, executor);
    }

    [Fact]
    public async Task Accessible_databases_are_returned()
    {
        var (service, _) = CreateService(
            FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"),
            FakeSqlQueryExecutor.DatabaseRow("Sales_Reporting"));

        var result = await service.DiscoverDatabasesAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(["AdventureWorks", "Sales_Reporting"], result.Databases.Select(d => d.Name));
        Assert.All(result.Databases, db => Assert.Equal("ONLINE", db.Status));
    }

    [Fact]
    public async Task System_databases_are_excluded_even_if_the_server_returns_them()
    {
        var (service, _) = CreateService(
            FakeSqlQueryExecutor.DatabaseRow("master"),
            FakeSqlQueryExecutor.DatabaseRow("model"),
            FakeSqlQueryExecutor.DatabaseRow("msdb"),
            FakeSqlQueryExecutor.DatabaseRow("tempdb"),
            FakeSqlQueryExecutor.DatabaseRow("TEMPDB"),
            FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var result = await service.DiscoverDatabasesAsync();

        Assert.Equal(["AdventureWorks"], result.Databases.Select(d => d.Name));
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("RESTORING")]
    [InlineData("RECOVERING")]
    [InlineData("SUSPECT")]
    [InlineData("EMERGENCY")]
    public async Task Databases_that_are_not_online_are_excluded(string state)
    {
        var (service, _) = CreateService(
            FakeSqlQueryExecutor.DatabaseRow("OfflineDb", state),
            FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var result = await service.DiscoverDatabasesAsync();

        Assert.Equal(["AdventureWorks"], result.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task Inaccessible_databases_are_excluded_by_the_discovery_query()
    {
        // HAS_DBACCESS filters these server-side, so an inaccessible database simply
        // never appears in the result set.
        var (service, executor) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var result = await service.DiscoverDatabasesAsync();

        Assert.Contains("HAS_DBACCESS(d.name) = 1", executor.ExecutedQueries.Single(), StringComparison.Ordinal);
        Assert.Contains("d.database_id > 4", executor.ExecutedQueries.Single(), StringComparison.Ordinal);
        Assert.Contains("d.state_desc = 'ONLINE'", executor.ExecutedQueries.Single(), StringComparison.Ordinal);
        Assert.Equal(["AdventureWorks"], result.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task Rows_without_a_name_are_ignored()
    {
        var (service, _) = CreateService(
            new SqlRow(new Dictionary<string, object?> { ["name"] = null, ["status"] = "ONLINE" }),
            FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var result = await service.DiscoverDatabasesAsync();

        Assert.Equal(["AdventureWorks"], result.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task Results_are_cached_until_invalidated()
    {
        var (service, executor) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        await service.DiscoverDatabasesAsync();
        await service.DiscoverDatabasesAsync();
        Assert.Equal(1, executor.ExecuteCount);

        service.InvalidateCache();
        await service.DiscoverDatabasesAsync();
        Assert.Equal(2, executor.ExecuteCount);
    }

    [Fact]
    public async Task Cache_can_be_bypassed()
    {
        var (service, executor) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        await service.DiscoverDatabasesAsync();
        await service.DiscoverDatabasesAsync(useCache: false);

        Assert.Equal(2, executor.ExecuteCount);
    }

    [Fact]
    public async Task Discovery_failure_is_sanitized()
    {
        var (service, executor) = CreateService();
        executor.ThrowOnExecute = new InvalidOperationException(
            "Login failed for user 'sqlpulse_reader' on server sql01 with password hunter2");

        var result = await service.DiscoverDatabasesAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SqlOperationStatus.Failed, result.Outcome.Status);
        Assert.Equal(SqlErrorSanitizer.GenericFailureMessage, result.Outcome.Message);
        Assert.DoesNotContain("hunter2", result.Outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlpulse_reader", result.Outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Databases);
    }

    [Fact]
    public async Task Discovery_is_not_attempted_when_sql_server_is_unconfigured()
    {
        var (service, executor) = CreateService();
        executor.IsConfigured = false;

        var result = await service.DiscoverDatabasesAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SqlErrorSanitizer.NotConfiguredMessage, result.Outcome.Message);
        Assert.Equal(0, executor.ExecuteCount);
    }

    [Fact]
    public async Task A_valid_accessible_database_passes_validation()
    {
        var (service, _) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        Assert.Null(await service.ValidateTargetDatabaseAsync("AdventureWorks"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_database_is_rejected(string? database)
    {
        var (service, _) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        Assert.Equal(
            DatabaseDiscoveryService.DatabaseRequiredMessage,
            await service.ValidateTargetDatabaseAsync(database));
    }

    [Theory]
    [InlineData("master'; DROP DATABASE Sales; --")]
    [InlineData("Sales];[master")]
    [InlineData("Sales OR 1=1")]
    [InlineData("Sales'")]
    [InlineData("Sales\"")]
    [InlineData("Sales;")]
    [InlineData("Sales DB")]
    [InlineData("données")]
    public async Task An_injection_attempt_is_rejected_before_reaching_sql_server(string database)
    {
        var (service, executor) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var error = await service.ValidateTargetDatabaseAsync(database);

        Assert.Equal(DatabaseDiscoveryService.InvalidFormatMessage, error);
        Assert.Equal(0, executor.ExecuteCount);
    }

    [Fact]
    public async Task A_name_longer_than_128_characters_is_rejected()
    {
        var (service, _) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var error = await service.ValidateTargetDatabaseAsync(new string('a', 129));

        Assert.Equal(DatabaseDiscoveryService.InvalidFormatMessage, error);
    }

    [Fact]
    public async Task An_unknown_database_is_rejected()
    {
        var (service, _) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var error = await service.ValidateTargetDatabaseAsync("NotDiscovered");

        Assert.Equal("Database 'NotDiscovered' is not accessible or does not exist.", error);
    }

    [Fact]
    public async Task A_system_database_is_never_a_valid_target()
    {
        var (service, _) = CreateService(FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"));

        var error = await service.ValidateTargetDatabaseAsync("master");

        Assert.Equal("Database 'master' is not accessible or does not exist.", error);
    }

    [Fact]
    public async Task Validation_fails_closed_when_discovery_is_unavailable()
    {
        var (service, executor) = CreateService();
        executor.ThrowOnExecute = new InvalidOperationException("boom");

        var error = await service.ValidateTargetDatabaseAsync("AdventureWorks");

        Assert.Equal(DatabaseDiscoveryService.DiscoveryUnavailableMessage, error);
    }

    [Theory]
    [InlineData("MyDatabase")]
    [InlineData("My_Database_01")]
    [InlineData("My-Database")]
    public void Valid_identifiers_are_accepted(string name) => Assert.True(DatabaseNames.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("My Database")]
    [InlineData("My.Database")]
    [InlineData("master;EXEC xp_cmdshell")]
    [InlineData("données")]
    public void Invalid_identifiers_are_rejected(string name) => Assert.False(DatabaseNames.IsValid(name));

    [Fact]
    public void An_identifier_of_exactly_128_characters_is_accepted() =>
        Assert.True(DatabaseNames.IsValid(new string('a', 128)));

    [Fact]
    public void Quoting_requires_a_valid_identifier()
    {
        Assert.Equal("[AdventureWorks]", DatabaseNames.Quote("AdventureWorks"));
        Assert.Throws<ArgumentException>(() => DatabaseNames.Quote("Sales];[master"));
    }
}
