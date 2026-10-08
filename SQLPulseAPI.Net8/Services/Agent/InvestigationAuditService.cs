using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Repositories;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>Builds, stores and retrieves the investigation audit trail.</summary>
public interface IInvestigationAuditService
{
    /// <summary>
    /// Records an investigation. Never throws.
    /// </summary>
    /// <remarks>
    /// Auditing is a record of work already done, so a storage failure must not discard an
    /// answer the caller is waiting for. Failures are logged and swallowed.
    /// </remarks>
    Task PersistAsync(InvestigationResponse response, CancellationToken cancellationToken = default);

    /// <summary>Lists recent audit records, newest first.</summary>
    Task<IReadOnlyList<InvestigationAuditRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves a single audit record, or <see langword="null"/> when unknown.</summary>
    Task<InvestigationAuditRecord?> GetAsync(
        string investigationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Investigation audit trail.
/// Ported from the audit helpers in <c>app/storage/history_service.py</c>.
/// </summary>
/// <remarks>
/// The audit answers "what did SQLPulse look at, and what did it conclude?" after the
/// fact. It stores the sanitized question and answer, the tools called with their
/// sanitized arguments, the findings referenced, and an explicit record that no
/// remediation occurred. Credentials, API keys and connection strings are never stored.
/// </remarks>
public sealed partial class InvestigationAuditService(
    IHistoryRepository repository,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<InvestigationAuditService> logger) : IInvestigationAuditService
{
    private readonly SqlServerOptions _sqlServer = sqlServerOptions.Value;

    /// <inheritdoc />
    public async Task PersistAsync(
        InvestigationResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var record = BuildRecord(response, _sqlServer.Server);
            await repository.SaveInvestigationAuditAsync(record, cancellationToken);

            logger.LogInformation(
                "Persisted audit for investigation {InvestigationId}",
                response.InvestigationId);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist investigation audit {InvestigationId} (non-fatal)",
                response.InvestigationId);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InvestigationAuditRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        repository.ListInvestigationAuditsAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<InvestigationAuditRecord?> GetAsync(
        string investigationId,
        CancellationToken cancellationToken = default) =>
        repository.GetInvestigationAuditAsync(investigationId, cancellationToken);

    /// <summary>
    /// Builds a sanitized audit record from an investigation.
    /// Ported from <c>build_audit_record</c>.
    /// </summary>
    /// <remarks>
    /// The question and answer are sanitized again on the way in. Both are free text that
    /// originated outside SQLPulse — one from a user, one from a model — and the audit
    /// store is long-lived, so a credential pasted into a question must not be kept.
    /// </remarks>
    internal static InvestigationAuditRecord BuildRecord(InvestigationResponse response, string server)
    {
        var (timeStart, timeEnd) = ExtractTimeRange(response.Question);

        return new InvestigationAuditRecord
        {
            InvestigationId = response.InvestigationId,
            CreatedAt = response.Timestamp,
            QuestionSanitized = LlmSanitizer.SanitizeText(response.Question),
            Server = server,
            Database = response.AffectedDatabase,
            TimeRangeStart = timeStart,
            TimeRangeEnd = timeEnd,
            ToolsCalled = response.ToolCallsDetail.Select(call => new AuditToolCall
            {
                ToolName = call.ToolName,

                // Only the sanitized arguments are kept. The raw arguments exist for the
                // live response; the durable record does not need them.
                ArgumentsSanitized = call.ArgumentsSanitized,
                ResultSummary = call.ResultSummary,
                Success = call.Success,
                Error = call.Error,
                DurationSeconds = call.DurationSeconds,
            }).ToList(),
            FindingReferences = ExtractFindingReferences(response),
            Severity = response.Severity,
            Confidence = response.Confidence,
            Answer = LlmSanitizer.SanitizeText(response.Answer),
            HumanReviewRequired = response.HumanReviewRequired,

            // Structurally true, not merely defaulted: no tool can modify a database.
            RemediationPerformed = false,
        };
    }

    /// <summary>
    /// Best-effort extraction of an ISO 8601 time range from the question.
    /// Ported from <c>_extract_time_range</c>.
    /// </summary>
    private static (string? Start, string? End) ExtractTimeRange(string text)
    {
        var matches = IsoDateTime().Matches(text);

        return matches.Count switch
        {
            0 => (null, null),
            1 => (matches[0].Value, null),
            _ => (matches[0].Value, matches[1].Value),
        };
    }

    /// <summary>
    /// Collects the query hashes and procedure names the investigation referenced.
    /// Ported from <c>_extract_finding_references</c>.
    /// </summary>
    /// <remarks>
    /// These make the audit searchable: "has SQLPulse looked at this query before?" is
    /// answerable without re-reading every stored answer.
    /// </remarks>
    private static List<string> ExtractFindingReferences(InvestigationResponse response)
    {
        var references = new List<string>();

        if (!string.IsNullOrEmpty(response.AffectedQuery))
        {
            references.Add(response.AffectedQuery);
        }

        foreach (var item in response.Evidence)
        {
            foreach (Match match in QueryHash().Matches(item))
            {
                if (!references.Contains(match.Value, StringComparer.Ordinal))
                {
                    references.Add(match.Value);
                }
            }
        }

        return references;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2})?")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"0x[0-9A-Fa-f]{4,}")]
    private static partial Regex QueryHash();
}
