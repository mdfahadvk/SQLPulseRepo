using System.Runtime.Serialization;

namespace SQLPulseAPI.Net8.Infrastructure;

/// <summary>
/// Outcome of a SQL operation, reusable by collectors and services so that a
/// permission problem is never reported as a generic failure.
/// </summary>
public enum SqlOperationStatus
{
    /// <summary>The operation completed and returned data.</summary>
    [EnumMember(Value = "success")]
    Success,

    /// <summary>The monitoring login lacks the permission required for this operation.</summary>
    [EnumMember(Value = "permission_denied")]
    PermissionDenied,

    /// <summary>The operation failed for a reason other than permissions.</summary>
    [EnumMember(Value = "failed")]
    Failed,

    /// <summary>The operation does not apply to the requested scope or server.</summary>
    [EnumMember(Value = "not_applicable")]
    NotApplicable,
}

/// <summary>
/// A sanitized SQL operation result: a status plus a message that is safe to return
/// to callers and safe to log.
/// </summary>
/// <param name="Status">Classification of the outcome.</param>
/// <param name="Message">Caller-safe description. Never contains credentials, server names or raw SQL.</param>
public readonly record struct SqlOperationOutcome(SqlOperationStatus Status, string Message)
{
    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => Status == SqlOperationStatus.Success;

    /// <summary>True when the monitoring login lacked a required permission.</summary>
    public bool IsPermissionIssue => Status == SqlOperationStatus.PermissionDenied;

    /// <summary>A successful outcome.</summary>
    public static SqlOperationOutcome Success(string message = "Operation completed successfully.") =>
        new(SqlOperationStatus.Success, message);
}
