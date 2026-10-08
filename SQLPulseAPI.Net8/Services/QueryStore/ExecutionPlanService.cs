using System.Globalization;
using System.Xml.Linq;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.QueryStore;

public sealed class ExecutionPlanService(
    IQueryStoreCollector queryStoreCollector,
    ISqlQueryExecutor executor,
    ILogger<ExecutionPlanService> logger) : IExecutionPlanService
{
    public Task<ExecutionPlanResponse> GetAsync(
        string database,
        string queryHash,
        long planId,
        bool includeXml = false,
        CancellationToken cancellationToken = default) =>
        GetAsync(database, queryHash, null, planId, includeXml, cancellationToken);

    public async Task<ExecutionPlanResponse> GetAsync(
        string database,
        string? queryHash,
        long? queryId,
        long? planId,
        bool includeXml = false,
        CancellationToken cancellationToken = default)
    {
        var response = new ExecutionPlanResponse
        {
            Database = database,
            QueryHash = queryHash,
            QueryId = queryId,
            PlanId = planId,
            RetrievedAt = DateTimeOffset.UtcNow,
        };

        if (!DatabaseNames.IsValid(database) || DatabaseNames.IsSystemDatabase(database))
        {
            response.Status = ExecutionPlanStatus.Unavailable;
            response.Message = "The database identifier is invalid.";
            return response;
        }

        if ((queryHash is not null && !IsQueryHash(queryHash))
            || (queryId is < 1)
            || (planId is < 1)
            || (queryHash is null && queryId is null && planId is null))
        {
            response.Status = ExecutionPlanStatus.NotFound;
            response.Message = "At least one valid query identifier is required.";
            return response;
        }

        var (queryStoreStatus, statusMessage) = await queryStoreCollector.CheckStatusAsync(
            database, cancellationToken);
        if (!queryStoreStatus.IsAccessible())
        {
            if (queryStoreStatus != QueryStoreStatus.PermissionDenied
                && queryHash is not null
                && queryId is null
                && planId is null)
            {
                return await TryCachedPlanAsync(response, queryHash, includeXml, cancellationToken);
            }

            response.Status = queryStoreStatus == QueryStoreStatus.PermissionDenied
                ? ExecutionPlanStatus.PermissionDenied
                : ExecutionPlanStatus.Unavailable;
            response.Message = statusMessage ?? "Query Store is not available for this database.";
            return response;
        }

        try
        {
            var rows = await executor.ExecuteReadQueryAsync(
                PerformanceQueries.ExecutionPlan,
                database,
                [queryHash, queryId, planId],
                cancellationToken);

            if (rows.Count > 1)
            {
                response.Status = ExecutionPlanStatus.NotFound;
                response.Message = "Multiple plans matched; supply plan_id to select one safely.";
                return response;
            }

            var row = rows.FirstOrDefault();
            var xml = row?.GetString("plan_xml");
            if (string.IsNullOrWhiteSpace(xml))
            {
                response.Status = ExecutionPlanStatus.NotFound;
                response.Message = "No execution plan was found for the supplied query hash and plan id.";
                return response;
            }

            response.Status = ExecutionPlanStatus.Available;
            response.PlanXml = includeXml ? xml : null;
            PlanXmlParser.Populate(response, xml);
            return response;
        }
        catch (Exception exception) when (IsPermissionError(exception))
        {
            logger.LogWarning(exception, "Execution plan access denied for {Database}", database);
            response.Status = ExecutionPlanStatus.PermissionDenied;
            response.Message = "Permission denied when reading the Query Store execution plan.";
            return response;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Execution plan retrieval failed for {Database}", database);
            response.Status = ExecutionPlanStatus.Unavailable;
            response.Message = "Execution plan retrieval failed; no plan was inferred.";
            return response;
        }
    }

    private async Task<ExecutionPlanResponse> TryCachedPlanAsync(
        ExecutionPlanResponse response,
        string queryHash,
        bool includeXml,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await executor.ExecuteReadQueryAsync(
                PerformanceQueries.CachedExecutionPlan,
                response.Database,
                [queryHash],
                cancellationToken);

            if (rows.Count != 1 || string.IsNullOrWhiteSpace(rows[0].GetString("plan_xml")))
            {
                response.Status = ExecutionPlanStatus.NotFound;
                response.Message = rows.Count > 1
                    ? "Multiple cached plans matched; a unique plan could not be selected safely."
                    : "No Query Store or uniquely matching cached execution plan was found.";
                return response;
            }

            var xml = rows[0].GetString("plan_xml")!;
            response.Status = ExecutionPlanStatus.Available;
            response.PlanXml = includeXml ? xml : null;
            PlanXmlParser.Populate(response, xml);
            response.Warnings.Add("Query Store was unavailable; this plan came from the current plan cache.");
            return response;
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Cached execution plan fallback unavailable for {Database}", response.Database);
            response.Status = ExecutionPlanStatus.Unavailable;
            response.Message = "Query Store and cached-plan metadata were unavailable; no plan was inferred.";
            return response;
        }
    }

    private static bool IsQueryHash(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && value.Length is >= 3 and <= 18
        && value[2..].All(Uri.IsHexDigit);

    private static bool IsPermissionError(Exception exception) =>
        exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase);
}

internal static class PlanXmlParser
{
    private const int MaximumFindings = 10;
    private const int HighestCostOperatorCount = 5;
    private const double MaterialCostPercent = 25;
    private const double MaterialRowCount = 100_000;

    public static void Populate(ExecutionPlanResponse response, string xml)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var relOps = document.Descendants().Where(element => element.Name.LocalName == "RelOp").ToList();
            var types = relOps
                .Select(element => element.Attribute("PhysicalOp")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();

            response.StatementCount = document.Descendants().Count(IsStatement);
            response.TotalOperatorCount = relOps.Count;
            var counts = types
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            response.OperatorCountsByType = counts.ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
            response.OperatorSummary = counts
                .Select(group => $"{group.Key} ({group.Count()})")
                .ToList();

            response.Parameters = document.Descendants()
                .Where(element => element.Name.LocalName is "ColumnReference" or "Parameter")
                .Where(element => element.Parent?.Name.LocalName == "ParameterList")
                .Select(ReadParameter)
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                .DistinctBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            response.Operators = relOps.Select(ReadOperator).ToList();
            response.HighestCostOperators = response.Operators
                .Where(op => op.EstimatedCostPercent is not null)
                .OrderByDescending(op => op.EstimatedCostPercent)
                .ThenByDescending(op => op.EstimatedRows)
                .Take(HighestCostOperatorCount)
                .ToList();

            response.PlanLimitations.Add("Estimated cost is relative optimizer cost within the plan, not elapsed time.");
            if (!document.Descendants().Any(element => element.Name.LocalName == "RunTimeCountersPerThread"))
            {
                response.PlanLimitations.Add("Actual execution statistics are unavailable.");
            }

            AddPlanWarnings(response, document);
            AddSpillFinding(response, document);
            AddImplicitConversionFinding(response, document);
            AddMemoryGrantFinding(response, document);
            AddMissingIndexFindings(response, document);
            AddOtherWarningFindings(response, document);

            foreach (var pair in relOps.Zip(response.Operators))
            {
                AddRowMismatchFinding(response, pair.First, pair.Second);
            }

            AddSignificantOperatorFindings(response);
            response.Findings = response.Findings
                .DistinctBy(finding => (finding.Category, finding.Operator, finding.Summary))
                .OrderBy(FindingPriority)
                .ThenByDescending(finding => SeverityRank(finding.Severity))
                .ThenByDescending(finding => ConfidenceRank(finding.Confidence))
                .Take(MaximumFindings)
                .ToList();
        }
        catch (Exception)
        {
            response.Message = "The execution plan was retrieved but could not be parsed safely.";
        }
    }

    private static bool IsStatement(XElement element) =>
        element.Name.LocalName is "StmtSimple" or "StmtCond" or "StmtCursor";

    private static ExecutionPlanOperator ReadOperator(XElement op)
    {
        var statement = op.Ancestors().FirstOrDefault(element => IsStatement(element));
        var statementCost = TryReadDouble(statement?.Attribute("StatementSubTreeCost")?.Value, out var totalCost)
            ? totalCost
            : 0;
        var operatorCost = TryReadDouble(op.Attribute("EstimatedTotalSubtreeCost")?.Value, out var cost)
            ? cost
            : (double?)null;
        var actualRows = op.Descendants()
            .Where(element => element.Name.LocalName == "RunTimeCountersPerThread")
            .Select(element => element.Attribute("ActualRows")?.Value)
            .Select(value => TryReadDouble(value, out var rows) ? rows : (double?)null)
            .Where(rows => rows is not null)
            .Select(rows => rows!.Value)
            .ToList();

        return new ExecutionPlanOperator
        {
            Type = op.Attribute("PhysicalOp")?.Value ?? "RelOp",
            Object = op.Descendants()
                .FirstOrDefault(element => element.Name.LocalName is "IndexScan" or "TableScan" or "IndexSeek")?
                .Attribute("Table")?.Value,
            EstimatedCostPercent = operatorCost is { } value && statementCost > 0
                ? Math.Round(value / statementCost * 100, 1)
                : null,
            EstimatedRows = TryReadDouble(op.Attribute("EstimateRows")?.Value, out var estimated)
                ? estimated
                : null,
            ActualRows = actualRows.Count > 0 ? actualRows.Sum() : null,
        };
    }

    private static ExecutionPlanParameter ReadParameter(XElement parameter)
    {
        var name = parameter.Attribute("Column")?.Value
            ?? parameter.Attribute("Name")?.Value
            ?? string.Empty;
        return new ExecutionPlanParameter
        {
            Name = name,
            DataType = parameter.Attribute("ParameterDataType")?.Value
                ?? parameter.Attribute("DataType")?.Value,
            CompiledValue = SafeParameterValue(name, parameter.Attribute("ParameterCompiledValue")?.Value),
            RuntimeValue = SafeParameterValue(name, parameter.Attribute("ParameterRuntimeValue")?.Value),
        };
    }

    private static string? SafeParameterValue(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase))
        {
            return "[REDACTED]";
        }

        const int maximumLength = 500;
        return value.Length <= maximumLength ? value : value[..maximumLength] + "…";
    }

    private static void AddRowMismatchFinding(
        ExecutionPlanResponse response,
        XElement op,
        ExecutionPlanOperator parsedOperator)
    {
        if (parsedOperator.EstimatedRows is not { } estimated
            || parsedOperator.ActualRows is not { } actualRows
            || estimated < 0
            || actualRows < 0)
        {
            return;
        }

        var ratio = Math.Max(estimated, actualRows) / Math.Max(Math.Min(estimated, actualRows), 0.0001);
        if (ratio < 10 || Math.Abs(actualRows - estimated) < 100)
        {
            return;
        }

        var severity = ratio >= 100 && Math.Abs(actualRows - estimated) >= 1_000 ? "high" : "medium";
        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = "CARDINALITY_ESTIMATE",
            Operator = op.Attribute("PhysicalOp")?.Value ?? "RelOp",
            Summary = "Estimated and actual row counts differ materially; review statistics and predicates that affect this operator.",
            Evidence =
            [
                $"Estimated rows: {FormatNumber(estimated)}",
                $"Actual rows: {FormatNumber(actualRows)}",
                $"Difference ratio: {ratio.ToString("G4", CultureInfo.InvariantCulture)}x",
            ],
            Confidence = "high",
            Severity = severity,
            Recommendation = "Review the row estimate, statistics, and predicate inputs with a human reviewer before changing the query or schema.",
        });
    }

    private static void AddSignificantOperatorFindings(ExecutionPlanResponse response)
    {
        var significant = response.Operators
            .Select(op => (Operator: op, Category: OperatorCategory(op.Type)))
            .Where(item => item.Category is not null
                && ((item.Operator.EstimatedCostPercent ?? 0) >= MaterialCostPercent
                    || (item.Operator.EstimatedRows ?? 0) >= MaterialRowCount))
            .GroupBy(item => item.Category!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in significant)
        {
            var ranked = group.OrderByDescending(item => item.Operator.EstimatedCostPercent ?? 0)
                .ThenByDescending(item => item.Operator.EstimatedRows ?? 0)
                .ToList();
            var top = ranked[0].Operator;
            var hasMaterialCost = (top.EstimatedCostPercent ?? 0) >= MaterialCostPercent;
            var summary = ranked.Count > 1
                ? $"{ranked.Count} {top.Type} operators were detected; the highest-cost operator has {(hasMaterialCost ? "a high relative estimated cost" : "high estimated row volume")} within its statement."
                : $"A {top.Type} operator has {(hasMaterialCost ? "a high relative estimated cost" : "high estimated row volume")} within its statement.";
            var evidence = new List<string>();
            if (top.EstimatedCostPercent is { } costPercent)
            {
                evidence.Add($"Relative estimated optimizer cost: {FormatNumber(costPercent)}% of the statement estimate");
            }
            if (top.EstimatedRows is { } estimatedRows)
            {
                evidence.Add($"Estimated rows: {FormatNumber(estimatedRows)}");
            }
            if (top.ActualRows is { } actualRows)
            {
                evidence.Add($"Actual rows: {FormatNumber(actualRows)}");
            }

            response.Findings.Add(new ExecutionPlanFinding
            {
                Category = group.Key,
                Operator = top.Type,
                Summary = summary + " This indicates optimizer-estimated work, not elapsed time or a proven root cause.",
                Evidence = evidence,
                Confidence = hasMaterialCost ? "medium" : "low",
                Severity = hasMaterialCost ? "medium" : "low",
                Recommendation = RecommendationFor(group.Key),
            });
        }
    }

    private static string? OperatorCategory(string type) => type switch
    {
        "Index Scan" or "Clustered Index Scan" => "INDEX_SCAN",
        "Index Seek" => "INDEX_SEEK",
        "Table Scan" => "TABLE_SCAN",
        "Sort" => "SORT",
        "Hash Match" => "HASH_JOIN",
        "Nested Loops" => "NESTED_LOOPS",
        "Spool" or "Table Spool" or "Index Spool" or "Row Count Spool" => "SPOOL",
        _ => "HIGH_COST_OPERATOR",
    };

    private static void AddSpillFinding(ExecutionPlanResponse response, XDocument document)
    {
        var spills = document.Descendants().Where(IsExplicitSpill).ToList();
        if (spills.Count == 0)
        {
            return;
        }

        var spillOperators = spills.Select(spill => spill.Ancestors().FirstOrDefault(element => element.Name.LocalName == "RelOp")?
                .Attribute("PhysicalOp")?.Value)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var details = spills.SelectMany(spill => spill.Attributes())
            .Where(attribute => attribute.Name.LocalName is "SpillLevel" or "SpilledThreadCount" or "SpilledRows" or "SpilledDataSize")
            .Select(attribute => $"{attribute.Name.LocalName}: {attribute.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = "SPILL",
            Operator = spillOperators.Count == 0 ? "Plan warning" : string.Join(", ", spillOperators),
            Summary = spills.Count > 1
                ? $"The plan explicitly reports {spills.Count} spill warnings. Review the affected operators and memory grant/runtime context."
                : "The plan explicitly reports a spill warning. Review the affected operator and memory grant/runtime context.",
            Evidence = details.Count == 0 ? ["Spill evidence is present in the plan XML."] : details,
            Confidence = "high",
            Severity = "high",
            Recommendation = "Review the spill details, memory grant, and concurrent workload with a human reviewer before considering query or configuration changes.",
        });
    }

    private static bool IsExplicitSpill(XElement element)
    {
        if (!element.Name.LocalName.Contains("Spill", StringComparison.OrdinalIgnoreCase))
        {
            return element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Contains("Spill", StringComparison.OrdinalIgnoreCase)
                && IsTrue(attribute.Value));
        }

        return element.Attributes().Any(attribute =>
                   attribute.Name.LocalName.Contains("Spill", StringComparison.OrdinalIgnoreCase)
                   && IsTrue(attribute.Value))
               || element.Name.LocalName.Equals("SpillToTempDb", StringComparison.OrdinalIgnoreCase)
                   && (TryReadDouble(element.Attribute("SpillLevel")?.Value, out var level) && level > 0
                       || TryReadDouble(element.Attribute("SpilledThreadCount")?.Value, out var threads) && threads > 0
                       || TryReadDouble(element.Attribute("SpilledRows")?.Value, out var rows) && rows > 0)
               || element.Name.LocalName.EndsWith("SpillDetails", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddImplicitConversionFinding(ExecutionPlanResponse response, XDocument document)
    {
        var conversions = document.Descendants()
            .Where(element => (element.Name.LocalName is "PlanAffectingConvert" or "ConvertIssue"
                    || !element.HasElements)
                && ContainsImplicitConversion(element))
            .ToList();
        if (conversions.Count == 0)
        {
            return;
        }

        var affectedTypes = conversions.Select(element => element.Ancestors()
                .FirstOrDefault(ancestor => ancestor.Name.LocalName == "RelOp")?
                .Attribute("PhysicalOp")?.Value)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = "IMPLICIT_CONVERSION",
            Operator = affectedTypes.Count == 0 ? "Plan expression" : string.Join(", ", affectedTypes),
            Summary = conversions.Count > 1
                ? $"The plan contains {conversions.Count} expressions with an explicit implicit-conversion marker. Review their effect on predicates and access paths."
                : "The plan contains an explicit implicit-conversion marker. Review its effect on predicates and access paths.",
            Evidence = conversions.Select(element => element.Attribute("Expression")?.Value
                    ?? element.Attributes().FirstOrDefault(attribute => attribute.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase))?.Value
                    ?? element.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .DefaultIfEmpty("CONVERT_IMPLICIT is present in the plan XML.")
                .ToList(),
            Confidence = "high",
            Severity = "medium",
            Recommendation = "Review the converted expression and parameter/column types with a human reviewer; confirm impact before changing query or schema design.",
        });
    }

    private static bool ContainsImplicitConversion(XElement element) =>
        element.DescendantsAndSelf().Any(candidate =>
            candidate.Attributes().Any(attribute => attribute.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase))
            || candidate.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase));

    private static void AddMemoryGrantFinding(ExecutionPlanResponse response, XDocument document)
    {
        var grants = document.Descendants().Where(element => element.Name.LocalName == "MemoryGrantInfo").ToList();
        var issues = grants.Select(grant =>
            {
                var granted = ReadLong(grant.Attribute("GrantedMemory")?.Value);
                var maxUsed = ReadLong(grant.Attribute("MaxUsedMemory")?.Value);
                var wait = ReadLong(grant.Attribute("GrantWaitTime")?.Value);
                var excessive = granted is > 0 && maxUsed is > 0
                    && granted.Value > maxUsed.Value * 2
                    && granted.Value - maxUsed.Value >= 102_400;
                var explicitWarning = grant.DescendantsAndSelf().Any(element =>
                    element.Name.LocalName.Contains("MemoryGrantWarning", StringComparison.OrdinalIgnoreCase)
                    || element.Attributes().Any(attribute =>
                        attribute.Name.LocalName.Contains("GrantWarning", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(attribute.Value)));
                return (Granted: granted, MaxUsed: maxUsed, Wait: wait, Excessive: excessive,
                    ExplicitWarning: explicitWarning);
            })
            .Where(grant => grant.Wait is > 0 || grant.Excessive || grant.ExplicitWarning)
            .ToList();
        var warningElements = document.Descendants()
            .Where(element => element.Name.LocalName == "MemoryGrantWarning")
            .ToList();
        if (issues.Count == 0 && warningElements.Count == 0)
        {
            return;
        }

        var evidence = new List<string>();
        foreach (var issue in issues)
        {
            if (issue.Granted is { } granted)
            {
                evidence.Add($"Granted memory: {granted.ToString("N0", CultureInfo.InvariantCulture)} KB");
            }
            if (issue.MaxUsed is { } maxUsed)
            {
                evidence.Add($"Maximum used memory: {maxUsed.ToString("N0", CultureInfo.InvariantCulture)} KB");
            }
            if (issue.Wait is > 0)
            {
                evidence.Add($"Memory grant wait: {issue.Wait} ms");
            }
        }
        evidence.AddRange(warningElements.Select(element => element.ToString(SaveOptions.DisableFormatting)));

        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = "MEMORY_GRANT",
            Operator = "Memory Grant",
            Summary = "The plan contains explicit memory grant warning, wait, or over-allocation evidence; validate its effect under workload concurrency.",
            Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            Confidence = "high",
            Severity = issues.Any(issue => issue.Wait is > 0) ? "high" : "medium",
            Recommendation = "Review the grant and workload concurrency evidence with a human reviewer before considering query or configuration changes.",
        });
    }

    private static void AddPlanWarnings(ExecutionPlanResponse response, XDocument document)
    {
        var warningDetails = document.Descendants()
            .Where(element => element.Name.LocalName == "Warnings")
            .SelectMany(element => element.Elements())
            .Select(FormatWarning)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var warning in warningDetails)
        {
            if (!response.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                response.Warnings.Add(warning);
        }
    }

    private static void AddMissingIndexFindings(ExecutionPlanResponse response, XDocument document)
    {
        var missing = document.Descendants().Where(element => element.Name.LocalName == "MissingIndexGroup").ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var impacts = missing.Select(element => element.Attribute("Impact")?.Value)
            .Where(value => TryReadDouble(value, out _))
            .Select(value => TryReadDouble(value, out var impact) ? impact : 0)
            .OrderDescending()
            .Take(3)
            .Select(impact => $"Plan missing-index estimated impact: {FormatPercent(impact)}%")
            .ToList();
        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = "MISSING_INDEX",
            Operator = "Missing Index",
            Summary = missing.Count > 1
                ? $"The plan explicitly contains {missing.Count} missing-index recommendations. Review them against current indexes and workload."
                : "The plan explicitly contains a missing-index recommendation. Review it against current indexes and workload.",
            Evidence = impacts.Count == 0 ? ["A MissingIndexGroup recommendation is present in the plan XML."] : impacts,
            Confidence = "high",
            Severity = "medium",
            Recommendation = "Review the missing-index recommendation against existing indexes and workload with a human reviewer before making any change.",
        });
    }

    private static void AddOtherWarningFindings(ExecutionPlanResponse response, XDocument document)
    {
        var warningElements = document.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "Warnings")
            .ToList();

        AddWarningFinding(response, warningElements, "NoJoinPredicate", "NO_JOIN_PREDICATE",
            "The plan explicitly reports a join without a join predicate; review whether this is intentional.");
        AddWarningFinding(response, warningElements, "ColumnsWithNoStatistics", "MISSING_STATISTICS",
            "The plan explicitly reports columns without statistics; validate estimates around the affected operators.");
        AddWarningFinding(response, warningElements, "UnmatchedIndexes", "UNMATCHED_INDEX_WARNING",
            "The plan explicitly reports unmatched index information; review the affected object and plan context.");
    }

    private static void AddWarningFinding(
        ExecutionPlanResponse response,
        IReadOnlyCollection<XElement> warnings,
        string elementName,
        string category,
        string summary)
    {
        var matching = warnings.Where(element => element.Name.LocalName == elementName).ToList();
        if (matching.Count == 0)
        {
            return;
        }

        response.Findings.Add(new ExecutionPlanFinding
        {
            Category = category,
            Operator = "Plan warning",
            Summary = summary,
            Evidence = matching.Select(element => element.ToString(SaveOptions.DisableFormatting)).Distinct().Take(3).ToList(),
            Confidence = "high",
            Severity = "medium",
            Recommendation = "Review the explicit plan warning with a human reviewer and validate it against query and runtime context.",
        });
    }

    private static string RecommendationFor(string category) => category switch
    {
        "INDEX_SCAN" or "TABLE_SCAN" => "Review whether an appropriate index could reduce the observed scan workload; compare with existing indexes and workload before making changes.",
        "SORT" => "Review the sort input rows, sort keys, memory grant, and runtime warnings with a human reviewer before changing query or index design.",
        "HASH_JOIN" => "Review join input rows, cardinality estimates, and runtime warnings with a human reviewer; operator presence alone does not establish a problem.",
        "SPOOL" => "Review why the plan reuses or stores rows at this operator and whether its measured workload impact is meaningful.",
        _ => "Review the operator's estimated work and query/runtime context with a human reviewer before changing query or schema design.",
    };

    private static int FindingPriority(ExecutionPlanFinding finding) => finding.Category switch
    {
        "SPILL" => 0,
        "MEMORY_GRANT" => 1,
        "IMPLICIT_CONVERSION" => 2,
        "CARDINALITY_ESTIMATE" => 3,
        "MISSING_INDEX" => 4,
        "NO_JOIN_PREDICATE" or "MISSING_STATISTICS" or "UNMATCHED_INDEX_WARNING" => 5,
        _ => 6,
    };

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static int ConfidenceRank(string confidence) => confidence.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static bool TryReadDouble(string? value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static long? ReadLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool IsTrue(string value) =>
        value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string FormatWarning(XElement element)
    {
        var attributes = element.Attributes()
            .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}")
            .ToList();
        return attributes.Count == 0
            ? element.Name.LocalName
            : $"{element.Name.LocalName} ({string.Join(", ", attributes)})";
    }

    private static string FormatNumber(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
