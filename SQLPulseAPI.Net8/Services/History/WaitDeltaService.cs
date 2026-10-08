using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Repositories;

namespace SQLPulseAPI.Net8.Services.History;

/// <summary>
/// Expresses cumulative wait counters as a change since the previous reading.
/// Ported from <c>app/storage/wait_stats_service.py</c>.
/// </summary>
/// <remarks>
/// <c>sys.dm_os_wait_stats</c> is cumulative since the last service restart, so a single
/// reading describes the server's entire uptime rather than its current state. Only the
/// change between two readings says anything about the recent interval.
/// </remarks>
public interface IWaitDeltaService
{
    /// <summary>
    /// Populates <paramref name="result"/> with a delta against the previous reading and
    /// stores the current one for next time.
    /// </summary>
    Task<WaitStatisticsResult> ApplyAsync(
        WaitStatisticsResult result,
        string server,
        string snapshotId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWaitDeltaService" />
public sealed class WaitDeltaService(
    IHistoryRepository repository,
    ILogger<WaitDeltaService> logger) : IWaitDeltaService
{
    /// <summary>
    /// Computes the delta between two readings, or explains why there is none.
    /// </summary>
    /// <remarks>
    /// This deliberately refuses to produce a number whenever one would mislead: no
    /// previous reading, a counter epoch change (restart), or counters that moved
    /// backwards. Reporting the reason is more useful than fabricating a value.
    /// </remarks>
    public static (WaitStatisticsDelta? Delta, WaitDeltaStatus Status) ComputeDelta(
        WaitStatisticsResult current,
        WaitTotalsRecord? previous)
    {
        if (current.Totals is null || previous is null)
        {
            return (null, WaitDeltaStatus.InsufficientPreviousData);
        }

        // A restart zeroes the counters, so subtracting across one would report a large
        // negative or a meaningless positive value.
        if (current.SqlServerStartTime is { } currentStart
            && previous.SqlServerStartTime is { } previousStart
            && currentStart != previousStart)
        {
            return (null, WaitDeltaStatus.ServerRestarted);
        }

        var waitDelta = current.Totals.WaitTimeMs - previous.WaitTimeMs;
        var tasksDelta = current.Totals.WaitingTasksCount - previous.WaitingTasksCount;
        var signalDelta = current.Totals.SignalWaitTimeMs - previous.SignalWaitTimeMs;
        var resourceDelta = current.Totals.ResourceWaitTimeMs - previous.ResourceWaitTimeMs;

        // Cumulative counters must never decrease. If they did, the epoch changed without
        // being observed (for example DBCC SQLPERF cleared them), so the comparison is void.
        if (Math.Min(Math.Min(waitDelta, tasksDelta), Math.Min(signalDelta, resourceDelta)) < 0)
        {
            return (null, WaitDeltaStatus.ServerRestarted);
        }

        var interval = (current.CollectedAt - previous.CollectedAt).TotalSeconds;

        return (
            new WaitStatisticsDelta
            {
                PreviousCollectedAt = previous.CollectedAt,
                IntervalSeconds = Math.Round(interval, 3, MidpointRounding.ToEven),
                WaitingTasksCount = tasksDelta,
                WaitTimeMs = waitDelta,
                SignalWaitTimeMs = signalDelta,
                ResourceWaitTimeMs = resourceDelta,
            },
            WaitDeltaStatus.Available);
    }

    /// <inheritdoc />
    public async Task<WaitStatisticsResult> ApplyAsync(
        WaitStatisticsResult result,
        string server,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (result.Totals is null || !result.Metadata.Success)
        {
            return result;
        }

        try
        {
            var previous = await repository.GetLatestWaitTotalsAsync(
                server, result.CollectedAt, cancellationToken);

            var (delta, status) = ComputeDelta(result, previous);
            result.Delta = delta;
            result.DeltaStatus = status;

            if (status == WaitDeltaStatus.ServerRestarted)
            {
                logger.LogInformation(
                    "Wait delta skipped for {Server}: counter epoch changed since the last reading", server);
            }

            await repository.SaveWaitTotalsAsync(
                new WaitTotalsRecord
                {
                    SnapshotId = snapshotId,
                    CollectedAt = result.CollectedAt,
                    Server = server,
                    SqlServerStartTime = result.SqlServerStartTime,
                    WaitingTasksCount = result.Totals.WaitingTasksCount,
                    WaitTimeMs = result.Totals.WaitTimeMs,
                    SignalWaitTimeMs = result.Totals.SignalWaitTimeMs,
                    ResourceWaitTimeMs = result.Totals.ResourceWaitTimeMs,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // A storage problem must never break a snapshot. The section reports that no
            // comparison was possible rather than carrying a number that was never computed.
            logger.LogWarning(exception, "Wait delta unavailable (storage error)");
            result.Delta = null;
            result.DeltaStatus = WaitDeltaStatus.InsufficientPreviousData;
        }

        return result;
    }
}
