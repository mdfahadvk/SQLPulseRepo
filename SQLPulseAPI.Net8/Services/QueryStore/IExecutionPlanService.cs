using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Services.QueryStore;

public interface IExecutionPlanService
{
    Task<ExecutionPlanResponse> GetAsync(
        string database,
        string? queryHash,
        long? queryId,
        long? planId,
        bool includeXml = false,
        CancellationToken cancellationToken = default);
}
