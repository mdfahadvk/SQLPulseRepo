using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests.Compatibility;

/// <summary>
/// The security posture of the migrated application, asserted as a whole.
/// </summary>
/// <remarks>
/// <para>
/// Individual guarantees are tested where they are implemented. This suite exists because
/// the property that matters is systemic: SQLPulse observes a SQL Server and never changes
/// it, and no path — REST, AI, or error message — may violate that or leak a credential.
/// </para>
/// <para>
/// These tests are deliberately structural. They enumerate the real route table and the
/// real tool catalogue rather than checking known-good examples, so a future endpoint or
/// tool that breaks the model fails here without anyone remembering to add a test.
/// </para>
/// </remarks>
public sealed class SecurityPostureTests
{
    private static SqlPulseApiFactory CreateFactory(FakeSqlQueryExecutor executor) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ISqlQueryExecutor>(executor);
                services.AddSingleton<IDatabaseDiscoveryService, DatabaseDiscoveryService>();
            },
        };

    // --- The application exposes no way to change anything --------------------

    [Fact]
    public void No_endpoint_mutates_the_database()
    {
        // Every route is a read except the investigation, which is a POST only because it
        // carries a question in the body. Nothing accepts PUT, PATCH or DELETE at all.
        using var factory = new SqlPulseApiFactory();
        using var _ = factory.CreateClient();

        var endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Route = endpoint.RoutePattern.RawText ?? string.Empty,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .Where(endpoint => !endpoint.Route.StartsWith("swagger", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(endpoints);

        var mutating = endpoints
            .Where(endpoint => endpoint.Methods.Any(method =>
                method is "PUT" or "PATCH" or "DELETE"))
            .Select(endpoint => endpoint.Route)
            .ToList();

        Assert.True(mutating.Count == 0, $"Mutating routes exposed: {string.Join(", ", mutating)}");

        var posts = endpoints
            .Where(endpoint => endpoint.Methods.Contains("POST"))
            .Select(endpoint => endpoint.Route)
            .ToList();

        Assert.Equal(["api/agent/investigate"], posts);
    }

    [Fact]
    public void No_endpoint_accepts_raw_sql()
    {
        // A route that took SQL from a caller would make every other control irrelevant.
        using var factory = new SqlPulseApiFactory();
        using var _ = factory.CreateClient();

        var endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        var routes = endpoints
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(routes, route =>
            route.Contains("execute", StringComparison.OrdinalIgnoreCase)
            || route.Contains("query/run", StringComparison.OrdinalIgnoreCase)
            || route.Contains("raw", StringComparison.OrdinalIgnoreCase)
            || route.Contains("kill", StringComparison.OrdinalIgnoreCase)
            || route.Contains("index", StringComparison.OrdinalIgnoreCase));

        // Route naming is only a signal, and a diagnostic route may legitimately mention
        // SQL — /api/sql-errors reports errors, it does not run them. What actually has to
        // hold is that no action binds a statement, from any source, under any name.
        var accepted = endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>())
            .Where(action => action is not null)
            .SelectMany(action => action!.Parameters.Select(parameter =>
                $"{action.ControllerName}.{action.ActionName}({parameter.Name})"))
            .Where(descriptor => SqlBearingParameterNames.Any(name =>
                descriptor.Contains($"({name})", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(accepted.Count == 0, $"Endpoints accepting SQL: {string.Join(", ", accepted)}");
    }

    /// <summary>Parameter names through which a caller could hand the API a statement.</summary>
    private static readonly string[] SqlBearingParameterNames =
    [
        "sql", "sqlText", "sql_text", "tsql", "t_sql", "statement", "statements",
        "script", "batch", "queryText", "query_text", "commandText", "command_text",
    ];

    [Theory]
    [InlineData("INSERT INTO dbo.Orders VALUES (1)")]
    [InlineData("UPDATE dbo.Orders SET Total = 0")]
    [InlineData("DELETE FROM dbo.Orders")]
    [InlineData("DROP TABLE dbo.Orders")]
    [InlineData("CREATE INDEX IX_Orders ON dbo.Orders(CustomerId)")]
    [InlineData("ALTER DATABASE AppDb SET QUERY_STORE = ON")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    [InlineData("EXEC sp_configure 'show advanced options', 1")]
    [InlineData("KILL 71")]
    [InlineData("DBCC FREEPROCCACHE")]
    [InlineData("BACKUP DATABASE AppDb TO DISK = 'x'")]
    [InlineData("SHUTDOWN")]
    [InlineData("xp_cmdshell 'dir'")]
    [InlineData("GRANT CONTROL SERVER TO sqlpulse")]
    public void Mutating_statements_are_rejected_before_execution(string sql)
    {
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.Orders")]
    [InlineData("/* harmless */ DELETE FROM dbo.Orders")]
    [InlineData("SELECT 1 -- comment\n; UPDATE dbo.Orders SET Total = 0")]
    public void A_mutation_hidden_behind_a_select_or_a_comment_is_still_rejected(string sql)
    {
        // Comments are stripped and batches split before checking, so the first token of
        // the statement is not the only thing examined.
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));
    }

    // --- The AI has no capability to change anything --------------------------

    [Fact]
    public void Every_agent_tool_is_a_read()
    {
        // The catalogue is the agent's entire capability surface, so it is enumerated
        // rather than sampled.
        Assert.NotEmpty(AgentToolDefinitions.All);

        Assert.All(AgentToolDefinitions.All, tool =>
        {
            Assert.True(
                ReadOnlyToolPrefixes.Any(prefix => tool.Name.StartsWith(prefix, StringComparison.Ordinal)),
                $"'{tool.Name}' does not begin with a read-only verb.");

            // The prefix is a convention; this is the part that matters. A verb implying a
            // change anywhere in the name fails, so 'get_and_kill_session' cannot slip
            // through on its prefix alone.
            var offending = tool.Name
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => MutatingToolVerbs.Contains(token, StringComparer.Ordinal))
                .ToList();

            Assert.True(offending.Count == 0, $"'{tool.Name}' implies mutation: {string.Join(", ", offending)}");
        });

        Assert.Equal(
            AgentToolDefinitions.All.Count,
            AgentToolDefinitions.ApprovedNames.Count);
    }

    /// <summary>
    /// Verbs a tool name may begin with. Both describe retrieval and analysis of evidence
    /// SQLPulse already holds; neither implies changing anything.
    /// </summary>
    private static readonly string[] ReadOnlyToolPrefixes = ["get_", "investigate_"];

    /// <summary>Verbs that would mean a tool changes something.</summary>
    private static readonly string[] MutatingToolVerbs =
    [
        "run", "execute", "exec", "create", "update", "delete", "drop", "alter", "kill",
        "set", "apply", "force", "enable", "disable", "rebuild", "truncate", "backup",
        "restore", "write", "modify", "remove", "add", "configure",
    ];

    [Theory]
    [InlineData("run_sql")]
    [InlineData("execute_sql")]
    [InlineData("create_index")]
    [InlineData("apply_index")]
    [InlineData("kill_session")]
    [InlineData("force_plan")]
    [InlineData("set_configuration")]
    [InlineData("update_statistics")]
    [InlineData("rebuild_index")]
    public void No_mutating_tool_is_approved(string toolName)
    {
        Assert.DoesNotContain(toolName, AgentToolDefinitions.ApprovedNames);
    }

    [Fact]
    public async Task An_unapproved_tool_never_reaches_sql_server()
    {
        var executor = new FakeSqlQueryExecutor();
        var llm = new ScriptedLlmClient();

        llm
            .ThenCalls(
                ("run_sql", """{"sql":"DROP TABLE dbo.Orders"}"""),
                ("create_index", """{"database":"AppDb"}"""))
            .ThenAnswers("Severity: low\nHuman Review Required: Yes");

        using var factory = new SqlPulseApiFactory
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ILlmClient>(llm);
                services.AddSingleton<ISqlQueryExecutor>(executor);
            },
        };

        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/agent/investigate",
            new StringContent(
                JsonSerializer.Serialize(new { question = "Drop the Orders table to fix it." }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, executor.ExecuteCount);
    }

    // --- Nothing sensitive leaves the process --------------------------------

    [Theory]
    [InlineData("Login failed for user 'sqlpulse' with Password=Sup3rSecret;")]
    [InlineData("Server=sql01;User Id=sqlpulse;Password=Sup3rSecret;")]
    [InlineData("Authorization: Bearer sk-live-0123456789abcdef")]
    [InlineData("api_key=sk-live-0123456789abcdef")]
    public void Secrets_are_stripped_from_text_before_it_can_be_stored_or_sent(string text)
    {
        var sanitized = LlmSanitizer.SanitizeText(text);

        Assert.DoesNotContain("Sup3rSecret", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-live-0123456789abcdef", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_failure_never_returns_credentials()
    {
        // The probe reports only whether it worked. Driver messages routinely echo the
        // server, the login and sometimes the whole connection string.
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnTestConnection = new InvalidOperationException(
                "Login failed for user 'sqlpulse'. Server=sql01;Password=Sup3rSecret;"),
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/database/connection-test");

        Assert.DoesNotContain("Sup3rSecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlpulse", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_collector_failure_never_returns_the_underlying_sql_error()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new InvalidOperationException(
                "Login failed. Server=sql01;User Id=sqlpulse;Password=Sup3rSecret;"),
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/api/performance/snapshot?scope=server");

        Assert.DoesNotContain("Sup3rSecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_response_ever_carries_the_llm_api_key()
    {
        const string ApiKey = "sk-live-must-never-appear";

        using var factory = new SqlPulseApiFactory
        {
            ConfigureTestServices = services =>
                services.Configure<SQLPulseAPI.Net8.Configuration.LlmOptions>(
                    options => options.ApiKey = ApiKey),
        };

        using var client = factory.CreateClient();

        foreach (var route in new[] { "/", "/health", "/database/connection-test", "/api/databases" })
        {
            var body = await (await client.GetAsync(route)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(ApiKey, body, StringComparison.Ordinal);
        }
    }

    // --- Database names are validated ----------------------------------------

    [Theory]
    [InlineData("master")]
    [InlineData("AppDb; DROP TABLE dbo.Orders")]
    [InlineData("AppDb'--")]
    [InlineData("[AppDb]")]
    [InlineData("")]
    public async Task A_database_name_that_is_not_discovered_is_refused(string database)
    {
        // Discovery is the allow-list. A name is never interpolated into a query on the
        // strength of looking well-formed.
        var executor = new FakeSqlQueryExecutor
        {
            Rows = [FakeSqlQueryExecutor.DatabaseRow("AppDb")],
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/analyze?scope=database&database={Uri.EscapeDataString(database)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("AppDb; DROP TABLE dbo.Orders")]
    [InlineData("AppDb'--")]
    [InlineData("a-name-that-is-far-too-long-to-be-a-sql-server-identifier-and-keeps-going-well-past-one-hundred-and-twenty-eight-characters-which-is-the-documented-limit")]
    public void A_malformed_identifier_is_rejected_by_the_name_validator(string database)
    {
        Assert.False(DatabaseNames.IsValid(database));
    }

    [Fact]
    public void System_databases_are_not_monitoring_targets()
    {
        foreach (var name in new[] { "master", "model", "msdb", "tempdb" })
        {
            Assert.True(DatabaseNames.IsSystemDatabase(name));
        }
    }
}
