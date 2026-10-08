using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.QueryStore;

/// <summary>Produces the historical slow-query response for a database and time range.</summary>
public interface ISlowHistoryService
{
    /// <summary>
    /// Reads Query Store history and returns classified, ranked candidates.
    /// </summary>
    /// <param name="database">Target database.</param>
    /// <param name="startTime">Start of the window.</param>
    /// <param name="endTime">End of the window.</param>
    /// <param name="thresholdMs">Slow-query monitoring threshold in milliseconds.</param>
    /// <param name="topN">Maximum Query Store rows to read.</param>
    /// <param name="groupResults">When true, duplicate intervals are merged per query/plan.</param>
    /// <param name="workloadType">When set, only candidates of this workload type are returned.</param>
    /// <param name="applicationTimeoutMs">
    /// Overrides the configured application timeout for this request. When neither is
    /// set, timeout classification is reported as unavailable rather than guessed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SlowHistoryResponse> GetSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        int thresholdMs,
        int topN = 20,
        bool groupResults = true,
        WorkloadType? workloadType = null,
        int? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared slow-history application service.
/// Ported from <c>app/services/slow_history_service.py</c>.
/// </summary>
/// <remarks>
/// The threshold selects which queries are slow enough to report. It is never reused as
/// the application timeout: when no timeout is supplied per request, the configured
/// <see cref="AnalysisOptions.ApplicationTimeoutMs"/> is used, and that stays unset
/// unless an operator has stated the application's real timeout.
/// </remarks>
public sealed class SlowHistoryService(
    IQueryStoreCollector collector,
    ISlowQueryCandidateBuilder candidateBuilder,
    IOptions<AnalysisOptions> analysisOptions,
    ILogger<SlowHistoryService> logger) : ISlowHistoryService
{
    private readonly AnalysisOptions _analysis = analysisOptions.Value;

    /// <inheritdoc />
    public async Task<SlowHistoryResponse> GetSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        int thresholdMs,
        int topN = 20,
        bool groupResults = true,
        WorkloadType? workloadType = null,
        int? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var applicationTimeout = applicationTimeoutMs ?? _analysis.ApplicationTimeoutMs;

        var response = new SlowHistoryResponse
        {
            Server = collector.ServerHost,
            Database = database,
            StartTime = startTime,
            EndTime = endTime,
            ThresholdMs = thresholdMs,
            SlowThresholdMs = thresholdMs,
            ApplicationTimeoutMs = applicationTimeout,
            TimeoutClassificationAvailable = applicationTimeout is not null,
            TopN = topN,
        };

        var read = await collector.CollectSlowHistoryAsync(
            database, startTime, endTime, thresholdMs, topN, cancellationToken);

        response.QueryStoreStatus = read.Status;

        if (!read.Status.IsAccessible())
        {
            // A disabled or unreadable Query Store is reported as a status and a message,
            // never as an error: the rest of SQLPulse still works without it.
            response.Message = read.Message;

            logger.LogInformation(
                "slow_history candidates: database={Database} start_time={StartTime} "
                + "end_time={EndTime} threshold_ms={ThresholdMs} top_n={TopN} "
                + "workload_type={WorkloadType} query_store_status={Status} candidate_count=0",
                database,
                startTime,
                endTime,
                thresholdMs,
                topN,
                WorkloadTypeLabel(workloadType),
                EnumMemberJsonConverter<QueryStoreStatus>.ToWireValue(read.Status));

            return response;
        }

        var candidates = await candidateBuilder.BuildAsync(
            read.Records,
            thresholdMs,
            database,
            groupResults,
            workloadType,
            applicationTimeout,
            cancellationToken);

        response.TotalCandidates = candidates.Count;
        response.SlowQueryCandidateCount = candidates.Count(c => c.IsSlowQueryCandidate);
        response.TimeoutCandidateCount = candidates.Count(c => c.IsTimeoutCandidate);
        response.Candidates = candidates;

        logger.LogInformation(
            "slow_history candidates: database={Database} start_time={StartTime} "
            + "end_time={EndTime} threshold_ms={ThresholdMs} "
            + "application_timeout_ms={ApplicationTimeoutMs} top_n={TopN} "
            + "workload_type={WorkloadType} query_store_status={Status} "
            + "candidate_count={CandidateCount} timeout_candidate_count={TimeoutCount}",
            database,
            startTime,
            endTime,
            thresholdMs,
            applicationTimeout?.ToString() ?? "not_configured",
            topN,
            WorkloadTypeLabel(workloadType),
            EnumMemberJsonConverter<QueryStoreStatus>.ToWireValue(read.Status),
            response.TotalCandidates,
            response.TimeoutCandidateCount);

        return response;
    }

    /// <summary>Renders the workload filter for logs, matching the Python wire values.</summary>
    private static string WorkloadTypeLabel(WorkloadType? workloadType) =>
        workloadType is { } value
            ? EnumMemberJsonConverter<WorkloadType>.ToWireValue(value)
            : "ALL";
}
