using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class ConfigurationTests
{
    private static IServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSqlPulseOptions(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_match_the_python_reference()
    {
        var provider = BuildProvider(new Dictionary<string, string?>());

        var application = provider.GetRequiredService<IOptions<ApplicationOptions>>().Value;
        var sql = provider.GetRequiredService<IOptions<SqlServerOptions>>().Value;
        var analysis = provider.GetRequiredService<IOptions<AnalysisOptions>>().Value;
        var history = provider.GetRequiredService<IOptions<HistoryOptions>>().Value;
        var llm = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
        var agent = provider.GetRequiredService<IOptions<AgentOptions>>().Value;

        Assert.Equal("SQLPulse", application.Name);
        Assert.Equal("0.1.0", application.Version);
        Assert.Equal("master", sql.DefaultDatabase);
        Assert.Equal(30, sql.ConnectionTimeoutSeconds);
        Assert.Equal(60, sql.CommandTimeoutSeconds);
        Assert.Equal(5000, analysis.SlowQueryThresholdMs);
        Assert.Null(analysis.ApplicationTimeoutMs);
        Assert.Equal(30, history.RetentionDays);
        Assert.Equal(5, history.BaselineMinSamples);
        Assert.Equal("sqlpulse_history.db", history.DatabasePath);
        Assert.Equal("openai", llm.Provider);
        Assert.Equal("gpt-4o", llm.Model);
        Assert.Equal(1500, llm.MaxTokens);
        Assert.Equal(4000, agent.MaxToolResultChars);
        Assert.Equal(10, agent.MaxToolCalls);
        Assert.Equal(60, agent.MaxExecutionSeconds);
    }

    [Fact]
    public void Sql_server_is_unconfigured_until_all_credentials_are_present()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["SqlServer:Server"] = "sql01",
            ["SqlServer:Username"] = "sqlpulse_reader",
        });

        Assert.False(provider.GetRequiredService<IOptions<SqlServerOptions>>().Value.IsConfigured);
    }

    [Fact]
    public void Sql_server_is_configured_when_all_credentials_are_present()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["SqlServer:Server"] = "sql01",
            ["SqlServer:Username"] = "sqlpulse_reader",
            ["SqlServer:Password"] = "not-a-real-password",
        });

        Assert.True(provider.GetRequiredService<IOptions<SqlServerOptions>>().Value.IsConfigured);
    }

    [Fact]
    public void Timeout_classification_requires_an_explicit_application_timeout()
    {
        var withoutTimeout = BuildProvider(new Dictionary<string, string?>())
            .GetRequiredService<IOptions<AnalysisOptions>>().Value;
        var withTimeout = BuildProvider(new Dictionary<string, string?>
        {
            ["Analysis:ApplicationTimeoutMs"] = "30000",
        }).GetRequiredService<IOptions<AnalysisOptions>>().Value;

        Assert.False(withoutTimeout.TimeoutClassificationAvailable);
        Assert.True(withTimeout.TimeoutClassificationAvailable);
        Assert.Equal(30000, withTimeout.ApplicationTimeoutMs);
    }

    [Fact]
    public void Workload_program_lists_are_split_and_trimmed()
    {
        var analysis = BuildProvider(new Dictionary<string, string?>
        {
            ["Analysis:WorkloadApplicationPrograms"] = "OrderService, .Net SqlClient ",
            ["Analysis:WorkloadBackgroundPrograms"] = "SQLAgent,,SSIS",
        }).GetRequiredService<IOptions<AnalysisOptions>>().Value;

        Assert.Equal(new[] { "OrderService", ".Net SqlClient" }, analysis.ApplicationProgramNames);
        Assert.Equal(new[] { "SQLAgent", "SSIS" }, analysis.BackgroundProgramNames);
    }

    [Fact]
    public void Invalid_slow_query_threshold_is_rejected()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Analysis:SlowQueryThresholdMs"] = "0",
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AnalysisOptions>>().Value);

        Assert.Contains(nameof(AnalysisOptions.SlowQueryThresholdMs), string.Join(" ", exception.Failures));
    }

    [Fact]
    public void Invalid_connection_timeout_is_rejected()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["SqlServer:ConnectionTimeoutSeconds"] = "0",
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SqlServerOptions>>().Value);
    }

    [Fact]
    public void Legacy_python_environment_names_are_translated()
    {
        var environment = new Hashtable
        {
            ["SQL_SERVER"] = "sql01",
            ["SQL_DEFAULT_DATABASE"] = "AdventureWorks",
            ["SLOW_QUERY_THRESHOLD_MS"] = "9000",
            ["LLM_MODEL"] = "gpt-4o-mini",
            ["MAX_AGENT_TOOL_CALLS"] = "4",
            ["NOT_A_SQLPULSE_SETTING"] = "ignored",
        };

        var translated = LegacyEnvironmentConfiguration.Translate(environment)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("sql01", translated["SqlServer:Server"]);
        Assert.Equal("AdventureWorks", translated["SqlServer:DefaultDatabase"]);
        Assert.Equal("9000", translated["Analysis:SlowQueryThresholdMs"]);
        Assert.Equal("gpt-4o-mini", translated["Llm:Model"]);
        Assert.Equal("4", translated["Agent:MaxToolCalls"]);
        Assert.DoesNotContain(translated, pair => pair.Value == "ignored");
    }

    [Fact]
    public void Legacy_debug_environment_value_is_ignored_when_not_boolean()
    {
        var environment = new Hashtable
        {
            ["DEBUG"] = "release",
        };

        var translated = LegacyEnvironmentConfiguration.Translate(environment)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.DoesNotContain("Application:Debug", translated.Keys);
    }
}
