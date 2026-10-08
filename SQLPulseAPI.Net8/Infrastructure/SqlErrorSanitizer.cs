using Microsoft.Data.SqlClient;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Infrastructure;

/// <summary>
/// Converts SQL Server exceptions into caller-safe outcomes.
/// </summary>
/// <remarks>
/// SQL Server error text routinely echoes back login names, database names, server
/// names and fragments of the failing statement. None of that may reach an HTTP
/// response, so callers only ever receive one of the fixed messages produced here.
/// The original exception is logged separately, at the point of failure.
/// </remarks>
public static class SqlErrorSanitizer
{
    /// <summary>
    /// SQL Server error numbers that mean "the login is not allowed to do this",
    /// as opposed to "this failed".
    /// </summary>
    private static readonly HashSet<int> PermissionErrorNumbers =
    [
        229,    // The <permission> permission was denied on the object
        230,    // The <permission> permission was denied on the column
        262,    // <permission> permission denied in database
        297,    // The user does not have permission to perform this action
        300,    // VIEW SERVER STATE permission was denied
        916,    // The server principal is not able to access the database under the current security context
        15247,  // User does not have permission to perform this action
        21089,  // The user does not have permission to run the procedure
    ];

    /// <summary>Message returned when the monitoring login lacks a permission.</summary>
    public const string PermissionDeniedMessage =
        "The monitoring account does not have the permissions required for this operation.";

    /// <summary>Message returned for any other SQL failure.</summary>
    public const string GenericFailureMessage =
        "The SQL Server operation could not be completed.";

    /// <summary>Message returned when SQL Server has not been configured.</summary>
    public const string NotConfiguredMessage =
        "SQL Server connection is not configured.";

    /// <summary>Classifies an exception and returns a caller-safe outcome.</summary>
    public static SqlOperationOutcome Sanitize(Exception exception) => exception switch
    {
        SqlException sql when IsPermissionError(sql) =>
            new SqlOperationOutcome(SqlOperationStatus.PermissionDenied, PermissionDeniedMessage),
        InvalidOperationException when exception is not ReadOnlyViolationException && IsNotConfigured(exception) =>
            new SqlOperationOutcome(SqlOperationStatus.Failed, NotConfiguredMessage),
        _ => new SqlOperationOutcome(SqlOperationStatus.Failed, GenericFailureMessage),
    };

    /// <summary>Returns <see langword="true"/> when the exception is a permission failure.</summary>
    public static bool IsPermissionError(SqlException exception)
    {
        if (IsPermissionErrorNumber(exception.Number))
        {
            return true;
        }

        foreach (SqlError error in exception.Errors)
        {
            if (IsPermissionErrorNumber(error.Number))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a SQL Server error number means the login
    /// lacked a permission rather than the operation having failed.
    /// </summary>
    public static bool IsPermissionErrorNumber(int number) => PermissionErrorNumbers.Contains(number);

    private static bool IsNotConfigured(Exception exception) =>
        exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase);
}
