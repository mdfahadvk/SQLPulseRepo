namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Maps the flat environment-variable names used by the Python implementation
/// (for example <c>SQL_SERVER</c>, <c>LLM_API_KEY</c>) onto the .NET configuration
/// keys, so an existing SQLPulse deployment can point at the .NET API without
/// rewriting its environment.
/// </summary>
/// <remarks>
/// Standard ASP.NET Core environment variables (<c>SqlServer__Password</c>) keep
/// working and take precedence because they are added by the default host builder
/// after this provider.
/// </remarks>
public static class LegacyEnvironmentConfiguration
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["APP_NAME"] = $"{ApplicationOptions.SectionName}:{nameof(ApplicationOptions.Name)}",
        ["APP_VERSION"] = $"{ApplicationOptions.SectionName}:{nameof(ApplicationOptions.Version)}",
        ["DEBUG"] = $"{ApplicationOptions.SectionName}:{nameof(ApplicationOptions.Debug)}",

        ["SQL_SERVER"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.Server)}",
        ["SQL_USERNAME"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.Username)}",
        ["SQL_PASSWORD"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.Password)}",
        ["SQL_DEFAULT_DATABASE"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.DefaultDatabase)}",
        ["SQL_ENCRYPT"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.Encrypt)}",
        ["SQL_TRUST_SERVER_CERTIFICATE"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.TrustServerCertificate)}",
        ["SQL_CONNECTION_TIMEOUT"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.ConnectionTimeoutSeconds)}",
        ["SQL_QUERY_TIMEOUT"] = $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.CommandTimeoutSeconds)}",

        ["SLOW_QUERY_THRESHOLD_MS"] = $"{AnalysisOptions.SectionName}:{nameof(AnalysisOptions.SlowQueryThresholdMs)}",
        ["APPLICATION_TIMEOUT_MS"] = $"{AnalysisOptions.SectionName}:{nameof(AnalysisOptions.ApplicationTimeoutMs)}",
        ["WORKLOAD_APPLICATION_PROGRAMS"] = $"{AnalysisOptions.SectionName}:{nameof(AnalysisOptions.WorkloadApplicationPrograms)}",
        ["WORKLOAD_BACKGROUND_PROGRAMS"] = $"{AnalysisOptions.SectionName}:{nameof(AnalysisOptions.WorkloadBackgroundPrograms)}",

        ["DETECTOR_LONG_RUNNING_WARNING_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.LongRunningWarningMs)}",
        ["DETECTOR_LONG_RUNNING_HIGH_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.LongRunningHighMs)}",
        ["DETECTOR_LONG_RUNNING_CRITICAL_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.LongRunningCriticalMs)}",
        ["DETECTOR_LONG_RUNNING_CRITICAL_COUNT"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.LongRunningCriticalCount)}",
        ["DETECTOR_BLOCKING_WARNING_WAIT_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.BlockingWarningWaitMs)}",
        ["DETECTOR_BLOCKING_HIGH_WAIT_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.BlockingHighWaitMs)}",
        ["DETECTOR_BLOCKING_CRITICAL_WAIT_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.BlockingCriticalWaitMs)}",
        ["DETECTOR_BLOCKING_CRITICAL_CHAIN_COUNT"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.BlockingCriticalChainCount)}",
        ["DETECTOR_CPU_HIGH_AVG_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuHighAvgMs)}",
        ["DETECTOR_CPU_CRITICAL_AVG_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuCriticalAvgMs)}",
        ["DETECTOR_CPU_HIGH_TOTAL_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuHighTotalMs)}",
        ["DETECTOR_CPU_CRITICAL_TOTAL_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuCriticalTotalMs)}",
        ["DETECTOR_CPU_MIN_EXECUTIONS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuMinExecutions)}",
        ["DETECTOR_CPU_MIN_SIGNIFICANT_AVG_MS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.CpuMinSignificantAvgMs)}",
        ["DETECTOR_IO_HIGH_AVG_LOGICAL_READS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoHighAvgLogicalReads)}",
        ["DETECTOR_IO_CRITICAL_AVG_LOGICAL_READS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoCriticalAvgLogicalReads)}",
        ["DETECTOR_IO_HIGH_TOTAL_LOGICAL_READS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoHighTotalLogicalReads)}",
        ["DETECTOR_IO_CRITICAL_TOTAL_LOGICAL_READS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoCriticalTotalLogicalReads)}",
        ["DETECTOR_IO_MIN_EXECUTIONS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoMinExecutions)}",
        ["DETECTOR_IO_MIN_SIGNIFICANT_AVG_READS"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.IoMinSignificantAvgReads)}",
        ["DETECTOR_REGRESSION_WARNING_MULTIPLIER"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.RegressionWarningMultiplier)}",
        ["DETECTOR_REGRESSION_HIGH_MULTIPLIER"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.RegressionHighMultiplier)}",
        ["DETECTOR_REGRESSION_CRITICAL_MULTIPLIER"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.RegressionCriticalMultiplier)}",
        ["DETECTOR_REGRESSION_MIN_SAMPLES"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.RegressionMinSamples)}",
        ["DETECTOR_REGRESSION_BASELINE_WINDOW"] = $"{DetectorThresholdOptions.SectionName}:{nameof(DetectorThresholdOptions.RegressionBaselineWindow)}",

        ["HISTORY_RETENTION_DAYS"] = $"{HistoryOptions.SectionName}:{nameof(HistoryOptions.RetentionDays)}",
        ["BASELINE_MIN_SAMPLES"] = $"{HistoryOptions.SectionName}:{nameof(HistoryOptions.BaselineMinSamples)}",
        ["HISTORY_DB_PATH"] = $"{HistoryOptions.SectionName}:{nameof(HistoryOptions.DatabasePath)}",

        ["SIGNOZ_ACCESS_TOKEN"] = $"{SqlErrorOptions.SectionName}:SigNoz:HeaderValue",
        ["SIGNOZ_HEADER_KEY"] = $"{SqlErrorOptions.SectionName}:SigNoz:HeaderKey",
        ["SIGNOZ_URL"] = $"{SqlErrorOptions.SectionName}:SigNoz:BaseUrl",
        ["SIGNOZ_SIGNAL"] = $"{SqlErrorOptions.SectionName}:SigNoz:Signal",

        ["LLM_PROVIDER"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.Provider)}",
        ["LLM_API_KEY"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.ApiKey)}",
        ["LLM_MODEL"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.Model)}",
        ["LLM_BASE_URL"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.BaseUrl)}",
        ["LLM_TEMPERATURE"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.Temperature)}",
        ["LLM_REQUEST_TIMEOUT_SECONDS"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.RequestTimeoutSeconds)}",
        ["LLM_MAX_TOKENS"] = $"{LlmOptions.SectionName}:{nameof(LlmOptions.MaxTokens)}",

        ["MAX_TOOL_RESULT_CHARS"] = $"{AgentOptions.SectionName}:{nameof(AgentOptions.MaxToolResultChars)}",
        ["MAX_AGENT_TOOL_CALLS"] = $"{AgentOptions.SectionName}:{nameof(AgentOptions.MaxToolCalls)}",
        ["MAX_AGENT_EXECUTION_SECONDS"] = $"{AgentOptions.SectionName}:{nameof(AgentOptions.MaxExecutionSeconds)}",
    };

    /// <summary>
    /// Adds an in-memory configuration source translating the legacy Python
    /// environment variable names into .NET configuration keys.
    /// </summary>
    public static IConfigurationBuilder AddLegacyEnvironmentVariables(this IConfigurationBuilder builder) =>
        builder.AddInMemoryCollection(Translate(Environment.GetEnvironmentVariables()));

    /// <summary>Translates a set of environment entries into configuration key/value pairs.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Translate(System.Collections.IDictionary environment)
    {
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            var name = entry.Key as string;
            if (name is null || !Map.TryGetValue(name, out var configurationKey))
            {
                continue;
            }

            var value = entry.Value as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (name.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
                && !bool.TryParse(value, out _))
            {
                continue;
            }

            yield return new KeyValuePair<string, string?>(configurationKey, value);
        }
    }
}
