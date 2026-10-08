using System.Text.RegularExpressions;

namespace SQLPulseAPI.Net8.Utilities;

/// <summary>
/// Raised when a query is rejected by <see cref="SqlSafety"/>.
/// </summary>
public sealed class ReadOnlyViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Centralized read-only enforcement for every statement SQLPulse sends to SQL Server.
/// Ported from <c>_validate_read_only</c> in <c>app/database/connection.py</c>.
/// </summary>
/// <remarks>
/// This is defence in depth, <b>not</b> the security boundary. The real boundary is the
/// SQL login itself, which must be granted read-only permissions
/// (<c>VIEW SERVER STATE</c> / <c>VIEW DATABASE STATE</c> and <c>db_datareader</c> at most).
/// String inspection can always be defeated; least-privilege credentials cannot.
/// </remarks>
public static partial class SqlSafety
{
    /// <summary>
    /// Statement prefixes that indicate a state-changing or otherwise unsafe command.
    /// Matches the Python <c>_BLOCKED_PREFIXES</c> tuple.
    /// </summary>
    /// <remarks>
    /// <c>SP_</c> and <c>XP_</c> cover stored-procedure entry points such as
    /// <c>sp_configure</c> and <c>xp_cmdshell</c>.
    /// </remarks>
    public static readonly IReadOnlyList<string> BlockedPrefixes =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "TRUNCATE",
        "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY", "MERGE", "BULK", "DBCC",
        "SHUTDOWN", "KILL", "BACKUP", "RESTORE", "SP_", "XP_",
    ];

    /// <summary>
    /// Throws <see cref="ReadOnlyViolationException"/> when <paramref name="query"/> appears
    /// to perform a write, an administrative action, or a procedure call.
    /// </summary>
    /// <remarks>
    /// Comments are stripped and batches are split on <c>;</c> before checking, so a
    /// mutating statement cannot be smuggled in behind a leading <c>SELECT</c> or a
    /// comment block.
    /// </remarks>
    public static void EnsureReadOnly(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ReadOnlyViolationException("Blocked empty SQL statement. SQLPulse only permits read-only queries.");
        }

        foreach (var statement in SplitStatements(StripComments(query)))
        {
            var normalized = statement.TrimStart('(', ' ', '\t', '\r', '\n').ToUpperInvariant();

            foreach (var prefix in BlockedPrefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new ReadOnlyViolationException(
                        $"Blocked potentially mutating SQL statement starting with '{prefix}'. "
                        + "SQLPulse only permits read-only queries.");
                }
            }
        }
    }

    /// <summary>Returns <see langword="true"/> when the query passes <see cref="EnsureReadOnly"/>.</summary>
    public static bool IsReadOnly(string? query)
    {
        try
        {
            EnsureReadOnly(query);
            return true;
        }
        catch (ReadOnlyViolationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SplitStatements(string query) =>
        query.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string StripComments(string query) =>
        LineCommentPattern().Replace(BlockCommentPattern().Replace(query, " "), " ");

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"--[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineCommentPattern();
}
