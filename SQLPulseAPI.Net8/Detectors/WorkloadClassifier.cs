using System.Text.RegularExpressions;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Classifies who or what runs a query, from whatever evidence is available.
/// Ported from the workload-classification half of <c>app/detectors/timeout_candidate.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Evidence is split by how much it can actually prove.
/// </para>
/// <para>
/// STRONG evidence identifies <em>what the connection is</em> (<c>program_name</c>, SQL
/// Agent job context) or <em>what the statement fundamentally does</em> (BACKUP, DBCC).
/// Those cannot reasonably belong to the other category.
/// </para>
/// <para>
/// WEAK evidence is naming convention and statement shape. A procedure called
/// <c>uspGetOrders</c> is probably application code, but <c>usp</c> is a convention a DBA
/// also uses for maintenance helpers, and a SELECT appears in every kind of workload.
/// Weak evidence is recorded and reported, but it never decides: attaching a
/// confident-looking label to a coin flip is worse than saying UNKNOWN.
/// </para>
/// </remarks>
public static class WorkloadClassifier
{
    private const RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions IgnoreDotAll = Ignore | RegexOptions.Singleline;

    /// <summary><c>program_name</c> values SQL Server Agent uses for job steps.</summary>
    private static readonly Regex[] AgentProgramPatterns =
    [
        new(@"SQLAgent", Ignore),
        new(@"SQL\s*Server\s*Agent", Ignore),
        new(@"SQLAGENT", Ignore),
    ];

    /// <summary>
    /// <c>program_name</c> values that identify an application or interactive client.
    /// These are not SQL Agent, so they establish the connection is application-side.
    /// </summary>
    private static readonly Regex[] ApplicationProgramPatterns =
    [
        new(@"\.Net\s*SqlClient", Ignore),
        new(@"Microsoft\.Data\.SqlClient", Ignore),
        new(@"EntityFramework", Ignore),
        new(@"\bJDBC\b", Ignore),
        new(@"jTDS", Ignore),
        new(@"go-mssqldb", Ignore),
        new(@"python|pyodbc|SQLAlchemy", Ignore),
        new(@"node-mssql|tedious", Ignore),
        new(@"Microsoft SQL Server Management Studio", Ignore),
        new(@"Azure Data Studio", Ignore),
        new(@"\bDBeaver\b", Ignore),
        new(@"\bsqlcmd\b", Ignore),
    ];

    /// <summary>Statements whose purpose is maintenance regardless of who issued them.</summary>
    private static readonly Regex[] MaintenanceStatementPatterns =
    [
        new(@"\bDBCC\b", Ignore),
        new(@"\bBACKUP\s+(DATABASE|LOG)\b", Ignore),
        new(@"\bRESTORE\s+(DATABASE|LOG)\b", Ignore),
        new(@"\bALTER\s+INDEX\b.*\b(REBUILD|REORGANIZE)\b", IgnoreDotAll),
        new(@"\bUPDATE\s+STATISTICS\b", Ignore),
        new(@"\bsp_updatestats\b", Ignore),
        new(@"\bsp_recompile\b", Ignore),
        new(@"\bDBCC\s+SHRINK", Ignore),
        new(@"\bALTER\s+DATABASE\b.*\bSET\b", IgnoreDotAll),
    ];

    /// <summary>Naming conventions — suggestive only, never decisive in either direction.</summary>
    private static readonly Regex[] WeakApplicationNamePatterns =
    [
        new(@"\busp[A-Z_]", Ignore),
        new(@"\bproc[A-Z_]", Ignore),
        new(@"\bsp[A-Z][a-z]", Ignore),
    ];

    private static readonly Regex[] WeakBackgroundNamePatterns =
    [
        new(@"maintenance", Ignore),
        new(@"cleanup", Ignore),
        new(@"retention", Ignore),
        new(@"purge", Ignore),
        new(@"archive", Ignore),
        new(@"\bnightly\b", Ignore),
        new(@"\bbatch\b", Ignore),
    ];

    /// <summary>Statement shapes present in every kind of workload.</summary>
    private static readonly Regex[] WeakStatementPatterns =
    [
        new(@"\bSELECT\b.*\bFROM\b", IgnoreDotAll),
        new(@"\bINSERT\b.*\bINTO\b", IgnoreDotAll),
        new(@"\bUPDATE\b.*\bSET\b", IgnoreDotAll),
        new(@"\bDELETE\b.*\bFROM\b", IgnoreDotAll),
        new(@"\bEXEC(UTE)?\b", IgnoreDotAll),
    ];

    /// <summary>
    /// Classifies a query's workload type and returns the formatted evidence behind it.
    /// Python: <c>classify_workload</c>.
    /// </summary>
    public static (WorkloadType WorkloadType, List<string> Evidence) Classify(
        string? sqlText,
        string? procedureName,
        string? programName = null,
        string? hostName = null,
        bool? isScheduledJob = null,
        IReadOnlyList<string>? applicationPrograms = null,
        IReadOnlyList<string>? backgroundPrograms = null)
    {
        var evidence = GatherEvidence(
            sqlText,
            procedureName,
            programName,
            hostName,
            isScheduledJob,
            applicationPrograms,
            backgroundPrograms);

        return (ResolveWorkloadType(evidence), evidence.Select(FormatEvidence).ToList());
    }

    /// <summary>
    /// Decides a workload type from gathered evidence. Python: <c>resolve_workload_type</c>.
    /// </summary>
    /// <remarks>
    /// Strong background evidence wins over strong application evidence: an application
    /// connection running BACKUP DATABASE is still doing maintenance, so what the
    /// statement does matters more than who called it.
    /// </remarks>
    public static WorkloadType ResolveWorkloadType(IReadOnlyList<WorkloadEvidence> evidence)
    {
        var strong = evidence.Where(e => e.Strength == EvidenceStrength.Strong).ToList();

        if (strong.Any(e => e.Supports == WorkloadType.BackgroundOrScheduled))
        {
            return WorkloadType.BackgroundOrScheduled;
        }

        if (strong.Any(e => e.Supports == WorkloadType.ApplicationOrInteractive))
        {
            return WorkloadType.ApplicationOrInteractive;
        }

        // No strong evidence: weak signals are reported but never promoted into an answer.
        return WorkloadType.Unknown;
    }

    /// <summary>Renders one evidence item. Python: <c>_format_evidence</c>.</summary>
    public static string FormatEvidence(WorkloadEvidence item) =>
        $"{EnumMemberJsonConverter<EvidenceStrength>.ToWireValue(item.Strength)}: [{item.Source}] {item.Detail}";

    /// <summary>
    /// Collects every workload signal available, tagged by strength.
    /// Python: <c>_gather_workload_evidence</c>.
    /// </summary>
    public static List<WorkloadEvidence> GatherEvidence(
        string? sqlText,
        string? procedureName,
        string? programName = null,
        string? hostName = null,
        bool? isScheduledJob = null,
        IReadOnlyList<string>? applicationPrograms = null,
        IReadOnlyList<string>? backgroundPrograms = null)
    {
        var evidence = new List<WorkloadEvidence>();

        // Strong: explicit scheduled-job knowledge from the caller.
        if (isScheduledJob is true)
        {
            evidence.Add(new WorkloadEvidence
            {
                Strength = EvidenceStrength.Strong,
                Source = "session",
                Detail = "Caller reported this execution ran as a scheduled job",
                Supports = WorkloadType.BackgroundOrScheduled,
            });
        }
        else if (isScheduledJob is false)
        {
            evidence.Add(new WorkloadEvidence
            {
                Strength = EvidenceStrength.Strong,
                Source = "session",
                Detail = "Caller reported this execution did not run as a scheduled job",
                Supports = WorkloadType.ApplicationOrInteractive,
            });
        }

        // Strong: program_name / application_name.
        if (!string.IsNullOrEmpty(programName))
        {
            evidence.AddRange(ClassifyProgramName(
                programName,
                applicationPrograms ?? [],
                backgroundPrograms ?? []));
        }
        else
        {
            // Recorded explicitly: Query Store keeps no session metadata, and saying so
            // is more useful than silently classifying on naming convention alone.
            evidence.Add(new WorkloadEvidence
            {
                Strength = EvidenceStrength.Weak,
                Source = "session",
                Detail = "No program_name/application_name available for this query",
                Supports = WorkloadType.Unknown,
            });
        }

        if (!string.IsNullOrEmpty(hostName))
        {
            evidence.Add(new WorkloadEvidence
            {
                Strength = EvidenceStrength.Weak,
                Source = "session",
                Detail = $"Executed from host '{hostName}'",
                Supports = WorkloadType.Unknown,
            });
        }

        // Strong: the statement's own purpose.
        var combined = string.Join(
            ' ',
            new[] { sqlText, procedureName }.Where(s => !string.IsNullOrEmpty(s)));

        if (!string.IsNullOrEmpty(sqlText))
        {
            foreach (var pattern in MaintenanceStatementPatterns)
            {
                if (pattern.IsMatch(sqlText))
                {
                    evidence.Add(new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Strong,
                        Source = "sql_text",
                        Detail = $"Maintenance statement matched /{pattern}/",
                        Supports = WorkloadType.BackgroundOrScheduled,
                    });
                }
            }
        }

        // Weak: naming conventions.
        if (!string.IsNullOrEmpty(procedureName))
        {
            foreach (var pattern in WeakApplicationNamePatterns)
            {
                if (pattern.IsMatch(procedureName))
                {
                    evidence.Add(new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Weak,
                        Source = "procedure_name",
                        Detail =
                            $"Name matches application naming convention /{pattern}/ — "
                            + "a convention, not proof of caller",
                        Supports = WorkloadType.ApplicationOrInteractive,
                    });
                }
            }

            foreach (var pattern in WeakBackgroundNamePatterns)
            {
                if (pattern.IsMatch(procedureName))
                {
                    evidence.Add(new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Weak,
                        Source = "procedure_name",
                        Detail =
                            $"Name suggests maintenance work /{pattern}/ — "
                            + "a naming hint, not proof of caller",
                        Supports = WorkloadType.BackgroundOrScheduled,
                    });
                }
            }
        }

        // Weak: generic statement shape. Only the first match is recorded, because
        // listing five ways of saying "this is a normal query" adds no information.
        if (!string.IsNullOrEmpty(sqlText))
        {
            foreach (var pattern in WeakStatementPatterns)
            {
                if (pattern.IsMatch(sqlText))
                {
                    evidence.Add(new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Weak,
                        Source = "sql_text",
                        Detail =
                            $"Generic statement shape /{pattern}/ — occurs in "
                            + "application and background workloads alike",
                        Supports = WorkloadType.Unknown,
                    });
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(combined) && string.IsNullOrEmpty(programName))
        {
            evidence.Add(new WorkloadEvidence
            {
                Strength = EvidenceStrength.Weak,
                Source = "none",
                Detail = "No SQL text, procedure name, or session metadata available",
                Supports = WorkloadType.Unknown,
            });
        }

        return evidence;
    }

    /// <summary>
    /// Derives workload evidence from a session program/application name.
    /// Python: <c>_classify_program_name</c>.
    /// </summary>
    /// <remarks>
    /// Operator-configured mappings are checked first and win outright: someone who knows
    /// the environment has stated what this program is, which beats any pattern SQLPulse
    /// ships with. Each match returns immediately so the strongest available answer is
    /// the only one reported.
    /// </remarks>
    private static List<WorkloadEvidence> ClassifyProgramName(
        string programName,
        IReadOnlyList<string> applicationPrograms,
        IReadOnlyList<string> backgroundPrograms)
    {
        foreach (var configured in backgroundPrograms)
        {
            if (programName.Contains(configured, StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Strong,
                        Source = "program_name",
                        Detail =
                            $"program_name '{programName}' matches configured "
                            + $"background program '{configured}'",
                        Supports = WorkloadType.BackgroundOrScheduled,
                    },
                ];
            }
        }

        foreach (var configured in applicationPrograms)
        {
            if (programName.Contains(configured, StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Strong,
                        Source = "program_name",
                        Detail =
                            $"program_name '{programName}' matches configured "
                            + $"application program '{configured}'",
                        Supports = WorkloadType.ApplicationOrInteractive,
                    },
                ];
            }
        }

        foreach (var pattern in AgentProgramPatterns)
        {
            if (pattern.IsMatch(programName))
            {
                return
                [
                    new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Strong,
                        Source = "program_name",
                        Detail =
                            $"program_name '{programName}' identifies a SQL Server "
                            + "Agent job step",
                        Supports = WorkloadType.BackgroundOrScheduled,
                    },
                ];
            }
        }

        foreach (var pattern in ApplicationProgramPatterns)
        {
            if (pattern.IsMatch(programName))
            {
                return
                [
                    new WorkloadEvidence
                    {
                        Strength = EvidenceStrength.Strong,
                        Source = "program_name",
                        Detail =
                            $"program_name '{programName}' identifies an application "
                            + "or interactive client",
                        Supports = WorkloadType.ApplicationOrInteractive,
                    },
                ];
            }
        }

        return
        [
            new WorkloadEvidence
            {
                Strength = EvidenceStrength.Weak,
                Source = "program_name",
                Detail =
                    $"program_name '{programName}' is not a recognised application "
                    + "or agent program",
                Supports = WorkloadType.Unknown,
            },
        ];
    }
}
