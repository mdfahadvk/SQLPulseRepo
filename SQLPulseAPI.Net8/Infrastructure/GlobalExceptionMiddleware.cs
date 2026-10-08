using System.Text.Json;
using SQLPulseAPI.Net8.Models.Responses;

namespace SQLPulseAPI.Net8.Infrastructure;

/// <summary>
/// Catch-all exception handler matching the Python global exception handler:
/// logs the failure server-side and returns a generic 500 payload so internal
/// details are never leaked to callers.
/// </summary>
public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    private const string GenericMessage = "An internal error occurred. Please try again later.";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception for {Method} {Path}",
                context.Request.Method,
                context.Request.Path.Value);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var payload = JsonSerializer.Serialize(
                new ErrorResponse { Detail = GenericMessage },
                SqlPulseJson.Options);

            await context.Response.WriteAsync(payload);
        }
    }
}
