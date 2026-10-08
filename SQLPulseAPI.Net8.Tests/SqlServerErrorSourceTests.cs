using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class SqlServerErrorSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    [Fact]
    public async Task Reads_error_reported_events_using_the_executor_parameter_contract()
    {
        var executor = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget>
                  <event name="error_reported" timestamp="2026-09-17T08:30:00Z">
                    <data name="error_number"><value>1205</value></data>
                    <data name="severity"><value>13</value></data>
                    <data name="state"><value>45</value></data>
                    <data name="message"><value>Transaction was deadlocked on lock resources.</value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                    <action name="client_app_name"><value>Merchandising.Api</value></action>
                    <action name="client_hostname"><value>app01</value></action>
                    <action name="session_id"><value>71</value></action>
                    <action name="sql_text"><value>UPDATE dbo.Products SET Price = Price</value></action>
                  </event>
                </RingBufferTarget>
                """)));

        var result = await CreateSource(executor).QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        var error = Assert.Single(result.Events);
        Assert.Equal(1205, error.SqlErrorNumber);
        Assert.Equal("Transaction was deadlocked on lock resources.", error.Message);
        Assert.Equal("Merchandising.Api", error.Application);
        Assert.Equal("system_health", error.SourceName);
        Assert.Equal(2, executor.ExecuteCount);
        Assert.All(executor.ExecutedQueries, sql => Assert.DoesNotContain("@session_name", sql));
        Assert.Contains("@p0", executor.ExecutedQueries[0], StringComparison.Ordinal);
        Assert.DoesNotContain("@p1", executor.ExecutedQueries[1], StringComparison.Ordinal);
        Assert.DoesNotContain("@p2", executor.ExecutedQueries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maps_xml_deadlock_report_payload_into_the_existing_message_field()
    {
        var executor = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget>
                  <event name="xml_deadlock_report" timestamp="2026-09-17T08:30:00Z">
                    <data name="xml_report"><value><deadlock><victim-list /></deadlock></value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                  </event>
                </RingBufferTarget>
                """)));

        var result = await CreateSource(executor).QueryAsync(Query());

        var error = Assert.Single(result.Events);
        Assert.Equal("xml_deadlock_report", error.EventName);
        Assert.Equal("<deadlock><victim-list /></deadlock>", error.Message);
        Assert.False(error.ApplicationTimeoutReported);
    }

    [Fact]
    public async Task Reads_namespaced_ring_buffer_payload_fields()
    {
        var executor = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget xmlns="http://schemas.microsoft.com/sqlserver/2004/07/eventdata">
                  <event name="error_reported" timestamp="2026-09-17T08:30:00Z">
                    <data name="error_number"><value>2627</value></data>
                    <data name="message"><value>Duplicate key violation.</value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                    <action name="client_app_name"><value>Orders.Api</value></action>
                  </event>
                </RingBufferTarget>
                """)));

        var result = await CreateSource(executor).QueryAsync(Query());

        var error = Assert.Single(result.Events);
        Assert.Equal(2627, error.SqlErrorNumber);
        Assert.Equal("Duplicate key violation.", error.Message);
        Assert.Equal("Merchandising", error.Database);
        Assert.Equal("Orders.Api", error.Application);
    }

    [Fact]
    public async Task Captures_explicit_execution_parameters_and_masks_sensitive_values()
    {
        var executor = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget>
                  <event name="error_reported" timestamp="2026-09-17T08:30:00Z">
                    <data name="error_number"><value>50000</value></data>
                    <data name="message"><value>Warehouse error.</value></data>
                    <data name="parameter_values"><value>@Whse = DAL; @CompanyID = 123; @Password = hunter2</value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                    <action name="object_name"><value>dbo.usp_Shipment</value></action>
                    <action name="query_hash"><value>0xABC123</value></action>
                    <action name="duration"><value>2500</value></action>
                  </event>
                </RingBufferTarget>
                """)));

        var result = await CreateSource(executor, captureParameterValues: true).QueryAsync(Query());

        var error = Assert.Single(result.Events);
        Assert.Equal("dbo.usp_Shipment", error.StoredProcedure);
        Assert.Equal("0xABC123", error.QueryHash);
        Assert.Equal(2.5, error.DurationMs);
        Assert.Equal("DAL", error.Parameters["@Whse"]);
        Assert.Equal("123", error.Parameters["@CompanyID"]);
        Assert.Equal("[REDACTED]", error.Parameters["@Password"]);
    }

    [Fact]
    public async Task Does_not_capture_parameters_when_telemetry_has_none_or_capture_is_disabled()
    {
        var executor = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget>
                  <event name="error_reported" timestamp="2026-09-17T08:30:00Z">
                    <data name="message"><value>Warehouse error.</value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                  </event>
                </RingBufferTarget>
                """)));

        var withoutTelemetry = await CreateSource(executor, captureParameterValues: true)
            .QueryAsync(Query());
        Assert.Empty(Assert.Single(withoutTelemetry.Events).Parameters);

        var withPayload = BuildExecutor(
            FakeSqlQueryExecutor.Row(("target_data", """
                <RingBufferTarget>
                  <event name="error_reported" timestamp="2026-09-17T08:30:00Z">
                    <data name="message"><value>Warehouse error.</value></data>
                    <data name="parameter_values"><value>@Whse = DAL</value></data>
                    <action name="database_name"><value>Merchandising</value></action>
                  </event>
                </RingBufferTarget>
                """)));
        var disabled = await CreateSource(withPayload, captureParameterValues: false).QueryAsync(Query());
        Assert.Empty(Assert.Single(disabled.Events).Parameters);
    }

    private static FakeSqlQueryExecutor BuildExecutor(SqlRow eventRow)
    {
        var executor = new FakeSqlQueryExecutor();
        executor.RowsForQuery = _ => executor.ExecuteCount == 1
            ? [FakeSqlQueryExecutor.Row(("session_exists", 1), ("session_running", 1))]
            : [eventRow];
        return executor;
    }

    private static SqlServerErrorSource CreateSource(
        FakeSqlQueryExecutor executor,
        bool captureParameterValues = false) =>
        new(
            executor,
            Options.Create(new SqlErrorOptions { CaptureParameterValues = captureParameterValues }),
            NullLogger<SqlServerErrorSource>.Instance);

    private static SqlErrorQuery Query() =>
        new("sql01", "Merchandising", Start, End);
}
