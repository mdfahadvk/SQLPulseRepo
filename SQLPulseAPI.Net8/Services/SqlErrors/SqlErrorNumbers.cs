using SQLPulseAPI.Net8.Models.SqlErrors;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>
/// The deterministic map from SQL Server error numbers to normalized categories.
/// </summary>
/// <remarks>
/// <para>
/// Kept in one table so classification is auditable and testable in isolation. An error
/// number is the strongest evidence available about what failed, so it always wins over
/// message text.
/// </para>
/// <para>
/// The table is deliberately incomplete. An unlisted number is reported as
/// <see cref="SqlErrorCategory.SqlExecutionError"/> — a truthful "SQL Server raised an
/// error we have not categorised" — rather than being forced into the nearest-looking
/// bucket, because a wrong category sends an investigation down the wrong path.
/// </para>
/// </remarks>
public static class SqlErrorNumbers
{
    /// <summary>
    /// The client-side command timeout surfaced by SQL Server data providers.
    /// </summary>
    /// <remarks>
    /// Not a SQL Server error: the provider raises it when the client stops waiting, which
    /// is why it is the one number that can confirm an application timeout.
    /// </remarks>
    public const int ClientCommandTimeout = -2;

    /// <summary>Transaction chosen as a deadlock victim.</summary>
    public const int DeadlockVictim = 1205;

    /// <summary>Lock request timed out inside SQL Server.</summary>
    public const int LockRequestTimeout = 1222;

    /// <summary>Login failed for user.</summary>
    public const int LoginFailed = 18456;

    private static readonly Dictionary<int, SqlErrorCategory> Map = new()
    {
        // Timeouts. -2 is raised by the client; 1222 is SQL Server abandoning a lock wait.
        [ClientCommandTimeout] = SqlErrorCategory.Timeout,
        [LockRequestTimeout] = SqlErrorCategory.Timeout,

        // Deadlocks.
        [DeadlockVictim] = SqlErrorCategory.Deadlock,

        // Connectivity and availability.
        [2] = SqlErrorCategory.Connection,       // Named Pipes / network path not found
        [53] = SqlErrorCategory.Connection,      // Network path was not found
        [64] = SqlErrorCategory.Connection,      // Connection was forcibly closed
        [121] = SqlErrorCategory.Connection,     // Semaphore timeout on the transport
        [233] = SqlErrorCategory.Connection,     // No process is on the other end of the pipe
        [258] = SqlErrorCategory.Connection,     // Transport-level wait operation timed out
        [4060] = SqlErrorCategory.Connection,    // Cannot open database requested by the login
        [10053] = SqlErrorCategory.Connection,   // Transport-level error, aborted by software
        [10054] = SqlErrorCategory.Connection,   // Existing connection forcibly closed
        [10060] = SqlErrorCategory.Connection,   // Network-related or instance-specific error
        [10061] = SqlErrorCategory.Connection,   // Connection refused
        [11001] = SqlErrorCategory.Connection,   // Host not found
        [40613] = SqlErrorCategory.Connection,   // Database is currently unavailable (Azure SQL)

        // Authentication.
        [18452] = SqlErrorCategory.LoginFailure, // Login from an untrusted domain
        [LoginFailed] = SqlErrorCategory.LoginFailure,
        [18470] = SqlErrorCategory.LoginFailure, // Account is disabled
        [18487] = SqlErrorCategory.LoginFailure, // Password has expired
        [18488] = SqlErrorCategory.LoginFailure, // Password must be changed

        // Authorization.
        [229] = SqlErrorCategory.PermissionDenied, // Permission denied on object
        [230] = SqlErrorCategory.PermissionDenied, // Permission denied on column
        [262] = SqlErrorCategory.PermissionDenied, // Permission denied in database
        [297] = SqlErrorCategory.PermissionDenied, // User does not have permission
        [300] = SqlErrorCategory.PermissionDenied, // VIEW SERVER STATE permission denied
        [916] = SqlErrorCategory.PermissionDenied, // Server principal cannot access the database

        // Constraint and data-integrity violations.
        [515] = SqlErrorCategory.ConstraintViolation,  // Cannot insert NULL
        [547] = SqlErrorCategory.ConstraintViolation,  // Constraint conflict
        [2601] = SqlErrorCategory.ConstraintViolation, // Duplicate key in a unique index
        [2627] = SqlErrorCategory.ConstraintViolation, // Unique constraint violation
        [544] = SqlErrorCategory.ConstraintViolation,  // Explicit identity insert not allowed

        // Type conversion and value fit.
        [241] = SqlErrorCategory.ConversionError,  // Conversion failed for a date/time
        [245] = SqlErrorCategory.ConversionError,  // Conversion failed converting a value
        [8114] = SqlErrorCategory.ConversionError, // Error converting data type
        [8152] = SqlErrorCategory.ConversionError, // String or binary data would be truncated
        [2628] = SqlErrorCategory.ConversionError, // Truncation, with column detail

        // Arithmetic.
        [220] = SqlErrorCategory.ArithmeticError,  // Arithmetic overflow for a data type
        [232] = SqlErrorCategory.ArithmeticError,  // Arithmetic overflow for numeric
        [8115] = SqlErrorCategory.ArithmeticError, // Arithmetic overflow converting a value
        [8134] = SqlErrorCategory.ArithmeticError, // Divide by zero

        // Missing objects and columns.
        [208] = SqlErrorCategory.InvalidObject,  // Invalid object name
        [2812] = SqlErrorCategory.InvalidObject, // Could not find stored procedure
        [4121] = SqlErrorCategory.InvalidObject, // Cannot find the function or aggregate
        [207] = SqlErrorCategory.InvalidColumn,  // Invalid column name
        [209] = SqlErrorCategory.InvalidColumn,  // Ambiguous column name

        // Transaction state.
        [266] = SqlErrorCategory.TransactionError,  // Transaction count mismatch leaving a procedure
        [1206] = SqlErrorCategory.TransactionError, // Distributed transaction manager failed
        [3902] = SqlErrorCategory.TransactionError, // COMMIT with no matching BEGIN
        [3903] = SqlErrorCategory.TransactionError, // ROLLBACK with no matching BEGIN
        [3930] = SqlErrorCategory.TransactionError, // Transaction is doomed
        [3960] = SqlErrorCategory.TransactionError, // Snapshot isolation update conflict
    };

    /// <summary>Error numbers with a known category.</summary>
    public static IReadOnlyCollection<int> KnownNumbers => Map.Keys;

    /// <summary>
    /// Returns the category for a SQL Server error number.
    /// </summary>
    /// <returns>
    /// The mapped category, <see cref="SqlErrorCategory.SqlExecutionError"/> for an
    /// unmapped number, or <see langword="null"/> when no number was reported at all.
    /// </returns>
    public static SqlErrorCategory? Categorize(int? errorNumber)
    {
        if (errorNumber is not { } number)
        {
            return null;
        }

        return Map.TryGetValue(number, out var category)
            ? category
            : SqlErrorCategory.SqlExecutionError;
    }

    /// <summary>True when the number has an explicit entry rather than falling back.</summary>
    public static bool IsKnown(int? errorNumber) => errorNumber is { } number && Map.ContainsKey(number);
}
