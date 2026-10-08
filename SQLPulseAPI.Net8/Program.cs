using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Repositories;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.Analysis;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Services.SqlErrors;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific overrides, including credentials. Kept in a separate file
// because appsettings.json is committed and this one is git-ignored, so a
// password or API key cannot reach source control by accident. Optional, so a
// deployment configured purely through environment variables still starts.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Legacy Python environment variable names (SQL_SERVER, LLM_API_KEY, ...) are
// translated first; the standard providers added by the host builder still win,
// so `SqlServer__Password` continues to override everything.
builder.Configuration.AddLegacyEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddSqlPulseOptions(builder.Configuration);

builder.Services.AddControllers()
    .AddJsonOptions(options => SqlPulseJson.Configure(options.JsonSerializerOptions));

builder.Services.AddSingleton<IDatabaseConnectionFactory, SqlServerConnectionFactory>();
builder.Services.AddSingleton<ISqlQueryExecutor, SqlQueryExecutor>();

// Singleton so the discovered database list is cached across requests, matching the
// module-level cache in the Python implementation.
builder.Services.AddSingleton<IDatabaseDiscoveryService, DatabaseDiscoveryService>();

// Collectors are stateless; every parameter is passed per call.
builder.Services.AddSingleton<ActiveRequestsCollector>();
builder.Services.AddSingleton<LongRunningQueriesCollector>();
builder.Services.AddSingleton<BlockingSessionsCollector>();
builder.Services.AddSingleton<TopCpuQueriesCollector>();
builder.Services.AddSingleton<TopIoQueriesCollector>();
builder.Services.AddSingleton<WaitStatisticsCollector>();
builder.Services.AddSingleton<IPerformanceSnapshotService, PerformanceSnapshotService>();

// SQLPulse's own historical storage. Singleton because the repository holds only an
// immutable connection string and opens a short-lived connection per operation, which
// is what makes concurrent request handling safe.
builder.Services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
builder.Services.AddSingleton<IHistoryService, HistoryService>();
builder.Services.AddSingleton<IBaselineService, BaselineService>();
builder.Services.AddSingleton<IWaitDeltaService, WaitDeltaService>();

// Detectors are stateless and thread-safe: everything they observe comes from the
// snapshot passed in, and exclusions are returned rather than accumulated on the
// instance, so concurrent requests cannot contaminate each other's results.
builder.Services.AddSingleton<LongRunningDetector>();
builder.Services.AddSingleton<BlockingDetector>();
builder.Services.AddSingleton<HighCpuDetector>();
builder.Services.AddSingleton<HighIoDetector>();
builder.Services.AddSingleton<QueryRegressionDetector>();
builder.Services.AddSingleton<IAnalysisEngine, AnalysisEngine>();

// Query Store history. Read-only throughout: SQLPulse reports Query Store's state and
// never enables, disables or reconfigures it.
builder.Services.AddSingleton<IQueryStoreCollector, QueryStoreCollector>();
builder.Services.AddSingleton<ISlowQueryCandidateBuilder, SlowQueryCandidateBuilder>();
builder.Services.AddSingleton<ISlowHistoryService, SlowHistoryService>();
builder.Services.AddSingleton<IExecutionPlanService, ExecutionPlanService>();

// SQL error investigation. SQLPulse owns no error feed of its own, so evidence arrives
// through ISqlErrorSource implementations registered here. With no application telemetry
// attached the API reports ERROR_SOURCE_UNAVAILABLE rather than an empty, reassuring list.
//
// The Query Store source is the one exception: it derives *unconfirmed* timeout candidates
// from evidence SQLPulse already reads, and is registered last so a real error feed is
// always reported first.
builder.Services.AddSingleton<ISqlErrorEventCache, SqlErrorEventCache>();

builder.Services.AddSingleton<ISqlErrorSource, SqlServerErrorSource>();

builder.Services.AddHttpClient<SigNozTelemetryClient>((provider, client) =>
{
    var options = provider.GetRequiredService<IOptions<SqlErrorOptions>>().Value;
    if (Uri.TryCreate(options.EffectiveSigNozBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
    {
        client.BaseAddress = baseUri;
    }

    client.Timeout = TimeSpan.FromSeconds(options.EffectiveSigNozTimeoutSeconds);
});

builder.Services.AddSingleton<ISqlErrorSource, SigNozErrorSource>();

builder.Services.AddSingleton<ISqlErrorSource, QueryStoreTimeoutCandidateSource>();

builder.Services.AddSingleton<ISqlErrorService, SqlErrorService>();
builder.Services.AddSingleton<ISqlErrorCorrelationService, SqlErrorCorrelationService>();

// AI investigation agent. The provider is reached over its OpenAI-compatible HTTP API,
// so OpenAI, Azure OpenAI and Groq are a configuration change rather than a code change.
// The handler timeout is set slightly above the configured request timeout so the
// client's own token fires first and a slow provider is reported as a timeout rather
// than as an ambiguous cancellation.
builder.Services.AddHttpClient(OpenAiCompatibleLlmClient.HttpClientName, (provider, client) =>
{
    var llmOptions = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(llmOptions.RequestTimeoutSeconds + 5);
});

builder.Services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();

// Agent tools delegate to the services above; they never issue SQL of their own.
builder.Services.AddSingleton<IAgentToolInvoker, AgentToolInvoker>();
builder.Services.AddSingleton<IInvestigationEngine, InvestigationEngine>();
builder.Services.AddSingleton<IInvestigationAuditService, InvestigationAuditService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "SQLPulse",
        Version = "0.1.0",
        Description =
            "Read-only SQL Server Performance Analysis Agent. "
            + "Supports server-level and database-level analysis.",
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

app.UseMiddleware<GlobalExceptionMiddleware>();

// Swagger is always on, matching the Python implementation which always serves /docs.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("./v1/swagger.json", "SQLPulse v1");
});

var uiPath = Path.Combine(AppContext.BaseDirectory, "SQLPulseUI");
if (Directory.Exists(uiPath))
{
    var uiFileProvider = new PhysicalFileProvider(uiPath);
    var uiIndexPath = Path.Combine(uiPath, "index.html");
    var uiAssetsPath = Path.GetFullPath(Path.Combine(uiPath, "assets"));
    var contentTypeProvider = new FileExtensionContentTypeProvider();

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = uiFileProvider,
        RequestPath = "/SQLPulseUI",
    });

    app.MapGet("/SQLPulseUI/assets/{**path}", (string? path) =>
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.NotFound();
        }

        var filePath = Path.GetFullPath(Path.Combine(uiAssetsPath, path));
        if (!filePath.StartsWith(uiAssetsPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(filePath))
        {
            return Results.NotFound();
        }

        if (!contentTypeProvider.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return Results.File(filePath, contentType);
    });

    app.MapGet("/SQLPulseUI/{**path}", async (HttpContext context, string? path) =>
    {
        if (!string.IsNullOrWhiteSpace(path) && Path.HasExtension(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        await context.Response.SendFileAsync(uiIndexPath);
    });
}

app.MapControllers();

// Create the history schema once at startup so the first snapshot does not pay for it.
// A storage failure is logged and tolerated: SQLPulse's primary job is reading the
// monitored server, and that does not depend on local history being writable.
try
{
    await app.Services.GetRequiredService<IHistoryRepository>().InitializeAsync();
}
catch (Exception exception)
{
    app.Logger.LogError(exception, "Historical storage could not be initialized; history endpoints will return 503");
}

var application = app.Services.GetRequiredService<IOptions<ApplicationOptions>>().Value;
app.Logger.LogInformation("{Application} v{Version} starting up", application.Name, application.Version);

app.Run();

/// <summary>Entry point exposed so the test host can bootstrap the application.</summary>
public partial class Program;
