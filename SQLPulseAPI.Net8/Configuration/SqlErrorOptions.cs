using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Settings for the SQL Errors capability.
/// </summary>
/// <remarks>
/// Nothing here can make SQLPulse write to SQL Server. The options select how far around
/// an incident to look and how much to keep in memory, not what to change.
/// </remarks>
public sealed class SqlErrorOptions
{
    public const string SectionName = "SqlErrors";

    /// <summary>
    /// How far either side of an error to look for performance evidence, in minutes.
    /// </summary>
    /// <remarks>
    /// Widening this finds more evidence and weakens every conclusion drawn from it,
    /// because more unrelated activity falls inside the window. Five minutes is narrow
    /// enough that a match is worth reporting.
    /// </remarks>
    [Range(1, 1440)]
    public int CorrelationWindowMinutes { get; set; } = 5;

    /// <summary>Default period, in hours, when a caller supplies no time range.</summary>
    [Range(1, 8760)]
    public int DefaultLookbackHours { get; set; } = 24;

    /// <summary>
    /// How many recently returned events stay resolvable by id.
    /// </summary>
    /// <remarks>
    /// SQLPulse does not persist error events, so the detail endpoint answers from the
    /// events a list query has just returned. Beyond this many, the oldest stop resolving
    /// and the caller is told to re-run the query rather than shown a guess.
    /// </remarks>
    [Range(1, 100_000)]
    public int EventCacheSize { get; set; } = 2_000;

    /// <summary>
    /// Whether SQLPulse may derive unconfirmed timeout candidates from its own Query Store
    /// evidence when no application telemetry is connected.
    /// </summary>
    /// <remarks>
    /// Derived candidates are always reported as unconfirmed. Turning this off leaves the
    /// SQL Errors tab reporting only what a real error feed observed.
    /// </remarks>
    public bool EnableQueryStoreTimeoutCandidates { get; set; } = true;
    public bool EnableSqlServerErrorSource { get; set; } = true;

    /// <summary>Enables the optional SigNoz application telemetry source.</summary>
    public bool SigNozEnabled { get; set; }

    /// <summary>Preferred nested SigNoz configuration and application registry.</summary>
    public SigNozOptions SigNoz { get; set; } = new();

    /// <summary>Base URL of the SigNoz instance, without the API path.</summary>
    public string SigNozBaseUrl { get; set; } = string.Empty;

    /// <summary>SigNoz service-account key. Never included in logs or response payloads.</summary>
    public string SigNozApiKey { get; set; } = string.Empty;

    /// <summary>Optional service.name filter applied by the SigNoz query.</summary>
    public string SigNozServiceName { get; set; } = string.Empty;

    /// <summary>Maximum time allowed for one SigNoz query.</summary>
    [Range(1, 300)]
    public int SigNozTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Whether explicit execution-time parameter values in Extended Events may be exposed.
    /// </summary>
    /// <remarks>
    /// Disabled by default. SQLPulse never reconstructs values from Query Store, plans or
    /// other executions, and values that look sensitive are masked before exposure.
    /// </remarks>
    public bool CaptureParameterValues { get; set; }

    public string ExtendedEventsSessionName { get; set; } = "system_health";

    public bool EffectiveSigNozEnabled => SigNoz.Enabled || SigNozEnabled;
    public string EffectiveSigNozBaseUrl => string.IsNullOrWhiteSpace(SigNoz.BaseUrl) ? SigNozBaseUrl : SigNoz.BaseUrl;
    public string EffectiveSigNozApiKey => !string.IsNullOrWhiteSpace(SigNoz.HeaderValue)
        ? SigNoz.HeaderValue
        : !string.IsNullOrWhiteSpace(SigNoz.ApiKey) ? SigNoz.ApiKey : SigNozApiKey;
    public string EffectiveSigNozAuthHeader => !string.IsNullOrWhiteSpace(SigNoz.HeaderKey)
        ? SigNoz.HeaderKey
        : string.IsNullOrWhiteSpace(SigNoz.AuthHeader) ? "SIGNOZ-API-KEY" : SigNoz.AuthHeader;
    public string EffectiveSigNozServiceName => !string.IsNullOrWhiteSpace(SigNoz.ServiceName)
        ? SigNoz.ServiceName
        : SigNozServiceName;
    public string EffectiveSigNozEnvironment => SigNoz.Environment;
    public string EffectiveSigNozSignal => string.Equals(SigNoz.Signal, "logs", StringComparison.OrdinalIgnoreCase)
        ? "logs"
        : "traces";
    public int EffectiveSigNozTimeoutSeconds => SigNoz.TimeoutSeconds > 0 && SigNoz.TimeoutSeconds != 15
        ? SigNoz.TimeoutSeconds
        : SigNozTimeoutSeconds;
    public IReadOnlyList<SigNozApplication> RegisteredApplications => SigNoz.Applications;
}
    
