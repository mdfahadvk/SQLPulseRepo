using System.Text.RegularExpressions;

namespace SQLPulseAPI.Net8.Utilities;

/// <summary>
/// Database identifier helpers ported from <c>validate_database_name</c> in
/// <c>app/database/connection.py</c> and <c>SYSTEM_DATABASES</c> in <c>app/config.py</c>.
/// </summary>
public static partial class DatabaseNames
{
    /// <summary>System databases excluded from monitoring.</summary>
    public static readonly IReadOnlySet<string> SystemDatabases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

    /// <summary>SQL Server's maximum identifier length.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>
    /// Returns <see langword="true"/> when the supplied name is a safe SQL Server database
    /// identifier: letters, digits, underscore and hyphen only, at most 128 characters.
    /// </summary>
    /// <remarks>
    /// Deliberately stricter than SQL Server itself. Anything that could terminate an
    /// identifier or a string literal — brackets, quotes, semicolons, whitespace — is
    /// rejected outright, matching the Python regex <c>^[A-Za-z0-9_\-]+$</c>.
    /// </remarks>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxIdentifierLength
        && IdentifierPattern().IsMatch(name);

    /// <summary>Returns <see langword="true"/> when the name is a SQL Server system database.</summary>
    public static bool IsSystemDatabase(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SystemDatabases.Contains(name);

    /// <summary>
    /// Bracket-quotes a validated identifier for the rare cases where a database name must
    /// appear in SQL text rather than as a parameter (for example <c>USE [db]</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The name failed <see cref="IsValid"/>.</exception>
    public static string Quote(string? name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("Invalid database name.", nameof(name));
        }

        // IsValid already excludes ']', so escaping is belt-and-braces.
        return $"[{name!.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
