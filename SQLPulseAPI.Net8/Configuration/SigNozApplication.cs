namespace SQLPulseAPI.Net8.Configuration;

/// <summary>Configuration-only mapping between an application and its databases.</summary>
public sealed class SigNozApplication
{
    public string Name { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public List<string> Databases { get; set; } = [];
    public List<string> Environment { get; set; } = [];
    public bool TelemetryEnabled { get; set; } = true;
}

public sealed class SigNozOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Preferred SigNoz authentication header, for example signoz-access-token.</summary>
    public string HeaderKey { get; set; } = string.Empty;
    /// <summary>Secret value supplied only by local configuration or environment.</summary>
    public string HeaderValue { get; set; } = string.Empty;
    /// <summary>Legacy single-service mapping used only when no registry is present.</summary>
    public string ServiceName { get; set; } = string.Empty;
    /// <summary>Legacy credential field retained for existing deployments.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
    public string AuthHeader { get; set; } = "SIGNOZ-API-KEY";
    /// <summary>Signal queried by the SQL error source: logs or traces.</summary>
    public string Signal { get; set; } = "traces";
    public string Environment { get; set; } = string.Empty;
    public List<SigNozApplication> Applications { get; set; } = [];
}
