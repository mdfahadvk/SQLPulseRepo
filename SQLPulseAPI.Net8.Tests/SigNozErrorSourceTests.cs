using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.SqlErrors;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class SigNozErrorSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    [Fact]
    public async Task Disabled_source_does_not_make_a_request()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var source = CreateSource(handler, enabled: false);

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Missing_access_token_is_not_configured_and_never_sends_a_request()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var options = Options.Create(new SqlErrorOptions
        {
            SigNoz = new SigNozOptions
            {
                Enabled = true,
                BaseUrl = "https://signoz.test",
                HeaderKey = "signoz-access-token",
                Applications = [new() { Name = "VendorMaintenanceAPI", ServiceName = "VendorMaintenanceAPI", Databases = ["NS_APP"] }],
            },
        });
        var source = new SigNozErrorSource(
            new SigNozTelemetryClient(new HttpClient(handler) { BaseAddress = new Uri("https://signoz.test/") },
                options, NullLogger<SigNozTelemetryClient>.Instance), options,
            NullLogger<SigNozErrorSource>.Instance);

        var result = await source.QueryAsync(Query("NS_APP"));

        Assert.Equal(SqlErrorSourceStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Maps_sql_exception_and_confirmed_timeout_fields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[
              {"timestamp":"2026-09-17T08:30:00Z","trace_id":"trace-1","correlation_id":"corr-1",
               "service.name":"Orders.Api","deployment.environment":"prod","host.name":"app01",
               "exception.type":"Microsoft.Data.SqlClient.SqlException","exception.message":"Execution Timeout Expired.",
               "http.route":"/orders","db.system":"mssql","db.namespace":"Merchandising",
               "db.operation.name":"dbo.usp_CreateOrder","duration_ms":1250,
               "command_timeout_reported":true}
            ]}
            """);
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        var error = Assert.Single(result.Events);
        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        Assert.Equal("Orders.Api", error.Application);
        Assert.Equal("prod", error.Environment);
        Assert.Equal("app01", error.Host);
        Assert.Equal("trace-1", error.TraceId);
        Assert.Equal("corr-1", error.CorrelationId);
        Assert.Equal("/orders", error.RequestEndpoint);
        Assert.Equal("dbo.usp_CreateOrder", error.StoredProcedure);
        Assert.Equal(1250, error.DurationMs);
        Assert.Equal("Microsoft.Data.SqlClient.SqlException", error.ExceptionType);
        Assert.True(error.ApplicationTimeoutReported);
        Assert.Contains("db.namespace", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ignores_non_sql_application_exceptions_and_preserves_empty_success()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[
              {"timestamp": "2026-09-17T08:30:00Z", "trace_id":"generic",
               "service.name":"Orders.Api", "exception.type":"ArgumentException",
               "exception.message":"bad input"}
            ]}
            """);
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authentication_failure_is_reported_without_exposing_provider_details(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status, "secret provider body");
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.PermissionDenied, result.Status);
        Assert.DoesNotContain("secret provider body", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_queries_only_mapped_services_and_reports_each_application()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var source = CreateSource(handler, enabled: true, applications:
        [
            new() { Name = "Orders API", ServiceName = "orders-api", Databases = ["Merchandising"] },
            new() { Name = "Reports API", ServiceName = "reports-api", Databases = ["Other"] },
        ]);

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        Assert.NotNull(result.ApplicationTelemetry);
        var telemetry = result.ApplicationTelemetry!;
        var application = Assert.Single(telemetry.Applications);
        Assert.Equal("orders-api", application.ServiceName);
        Assert.Equal(ApplicationTelemetryStatus.NoData, application.Status);
        Assert.Contains("orders-api", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("reports-api", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_parameters_are_captured_and_sensitive_values_are_masked()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[{"timestamp":"2026-09-17T08:30:00Z","service.name":"orders-api",
             "db.system":"mssql","db.namespace":"Merchandising","exception.message":"SQL error",
             "parameters":{"@CompanyID":123,"@Password":"do-not-show"}}]}
            """);
        var source = CreateSource(handler, enabled: true, capture: true, applications:
            [new() { Name = "Orders API", ServiceName = "orders-api", Databases = ["Merchandising"] }]);

        var error = Assert.Single((await source.QueryAsync(Query(database: "Merchandising"))).Events);

        Assert.Equal("123", error.Parameters["@CompanyID"]);
        Assert.Equal("[REDACTED]", error.Parameters["@Password"]);
    }

    [Theory]
    [InlineData("NS_APP")]
    [InlineData("NSWeb")]
    public async Task Vendor_maintenance_mapping_scopes_service_and_environment(string database)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""
            {"data":[
              {"timestamp":"2026-09-17T08:30:00Z","service.name":"VendorMaintenanceAPI",
               "deployment.environment":"DEVERP","db.system":"mssql","db.namespace":"{{database}}",
               "exception.type":"Microsoft.Data.SqlClient.SqlException","exception.message":"SQL error"},
              {"timestamp":"2026-09-17T08:30:00Z","service.name":"UnrelatedApi",
               "deployment.environment":"DEVERP","db.system":"mssql","db.namespace":"NS_APP",
               "exception.type":"Microsoft.Data.SqlClient.SqlException","exception.message":"SQL error"}
            ]}
            """);
        var options = Options.Create(new SqlErrorOptions
        {
            SigNoz = new SigNozOptions
            {
                Enabled = true,
                BaseUrl = "https://signoz.test",
                HeaderValue = "api-key",
                HeaderKey = "signoz-access-token",
                Applications = [new()
                {
                    Name = "VendorMaintenanceAPI", ServiceName = "VendorMaintenanceAPI",
                    Databases = ["NS_APP", "NSWeb"], Environment = ["DEVERP"],
                }],
            },
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://signoz.test/") };
        var source = new SigNozErrorSource(new SigNozTelemetryClient(client, options, NullLogger<SigNozTelemetryClient>.Instance),
            options, NullLogger<SigNozErrorSource>.Instance);

        var result = await source.QueryAsync(Query(database, application: "VendorMaintenanceAPI"));

        var error = Assert.Single(result.Events);
        Assert.Equal("VendorMaintenanceAPI", error.Application);
        Assert.Equal("DEVERP", error.Environment);
        Assert.Equal(database, result.ApplicationTelemetry?.Applications.Single().Database);
        using var queryBody = JsonDocument.Parse(handler.LastBody);
        var expression = queryBody.RootElement.GetProperty("compositeQuery").GetProperty("queries")[0]
            .GetProperty("spec").GetProperty("filter").GetProperty("expression").GetString();
        Assert.Contains("service.name = 'VendorMaintenanceAPI'", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("db.namespace =", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("deployment.environment =", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("UnrelatedApi", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("api-key", handler.LastToken);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("api/v5/query_range", handler.LastPath);
    }

    [Theory]
    [InlineData("NSWeb")]
    [InlineData("NS_APP")]
    public async Task Mapped_vendor_service_with_no_sql_errors_is_available_for_selected_database(string database)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var source = CreateSource(handler, enabled: true, applications:
        [
            new()
            {
                Name = "VendorMaintenanceAPI", ServiceName = "VendorMaintenanceAPI",
                Databases = ["NSWeb", "NS_APP"], Environment = ["DEVERP"],
            },
        ]);

        var result = await source.QueryAsync(Query(database));

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        var application = Assert.Single(result.ApplicationTelemetry!.Applications);
        Assert.Equal("VendorMaintenanceAPI", application.Name);
        Assert.Equal("VendorMaintenanceAPI", application.ServiceName);
        Assert.Equal(database, application.Database);
        Assert.Equal(ApplicationTelemetryStatus.NoData, application.Status);
    }

    [Fact]
    public async Task Unmapped_database_is_not_associated_with_registry_application()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var options = Options.Create(new SqlErrorOptions
        {
            SigNoz = new SigNozOptions
            {
                Enabled = true, BaseUrl = "https://signoz.test", HeaderValue = "api-key",
                Applications = [new() { Name = "VendorMaintenanceAPI", ServiceName = "VendorMaintenanceAPI", Databases = ["NS_APP", "NSWeb"] }],
            },
        });
        var source = new SigNozErrorSource(new SigNozTelemetryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://signoz.test/") }, options,
            NullLogger<SigNozTelemetryClient>.Instance), options,
            NullLogger<SigNozErrorSource>.Instance);

        var result = await source.QueryAsync(Query("OtherDb"));

        Assert.Equal(SqlErrorSourceStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Keeps_events_without_optional_environment_and_database_attributes_without_fabricating_fields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[{"timestamp":"2026-09-17T08:30:00Z","service.name":"Orders.Api",
             "exception.type":"Microsoft.Data.SqlClient.SqlException","exception.message":"SQL error"}]}
            """);
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var error = Assert.Single((await source.QueryAsync(Query("Merchandising"))).Events);

        Assert.Null(error.Database);
        Assert.Null(error.Environment);
        Assert.DoesNotContain("deployment.environment =", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drops_events_with_a_database_that_does_not_match_the_selected_database()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[{"timestamp":"2026-09-17T08:30:00Z","service.name":"Orders.Api",
             "db.namespace":"Other","exception.type":"Microsoft.Data.SqlClient.SqlException",
             "exception.message":"SQL error"}]}
            """);
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query("Merchandising"));

        Assert.Empty(result.Events);
        Assert.Equal(ApplicationTelemetryStatus.NoData, result.ApplicationTelemetry?.Status);
    }

    [Fact]
    public async Task Provider_failure_is_reported_as_failed_source()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, "internal details");
        var source = CreateSource(handler, enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.Failed, result.Status);
        Assert.DoesNotContain("internal details", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_timeout_is_reported_as_failed_source()
    {
        var source = CreateSource(new TimeoutHandler(), enabled: true, database: "Merchandising");

        var result = await source.QueryAsync(Query(database: "Merchandising"));

        Assert.Equal(SqlErrorSourceStatus.Failed, result.Status);
    }

    private static SigNozErrorSource CreateSource(
        HttpMessageHandler handler,
        bool enabled,
        bool capture = false,
        List<SigNozApplication>? applications = null,
        string database = "Merchandising")
    {
        var options = Options.Create(new SqlErrorOptions
        {
            SigNozEnabled = enabled,
            SigNozBaseUrl = "https://signoz.test",
            SigNozApiKey = "api-key",
            SigNozServiceName = "Orders.Api",
            SigNozTimeoutSeconds = 1,
            CaptureParameterValues = capture,
        });
        options.Value.SigNoz.Applications = applications ??
        [new() { Name = "Orders.Api", ServiceName = "Orders.Api", Databases = [database] }];
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://signoz.test/") };
        var telemetryClient = new SigNozTelemetryClient(
            client,
            options,
            NullLogger<SigNozTelemetryClient>.Instance);
        return new SigNozErrorSource(
            telemetryClient,
            options,
            NullLogger<SigNozErrorSource>.Instance);
    }

    private static SqlErrorQuery Query(string? database = null, string? application = null) =>
        new("sql01", database, Start, End, Application: application);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public string LastToken { get; private set; } = string.Empty;
        public HttpMethod? LastMethod { get; private set; }
        public string LastPath { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastToken = request.Headers.TryGetValues("signoz-access-token", out var token)
                ? token.Single()
                : string.Empty;
            Assert.True(request.Headers.Contains("SIGNOZ-API-KEY") || request.Headers.Contains("signoz-access-token"));
            var authHeader = request.Headers.Contains("SIGNOZ-API-KEY") ? "SIGNOZ-API-KEY" : "signoz-access-token";
            Assert.Equal("api-key", request.Headers.GetValues(authHeader).Single());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out"));
    }
}
