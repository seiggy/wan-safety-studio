using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Server;

/// <summary>Maps domain errors to HTTP status codes with the same {"error": message} body the UI already expects.</summary>
public sealed class ErrorMappingMiddleware(RequestDelegate next, ILogger<ErrorMappingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception error) when (!context.Response.HasStarted && Map(error) is { } status)
        {
            if (status >= 500)
            {
                logger.LogError(error, "Unhandled error for {Path}", context.Request.Path);
            }

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = status >= 500 ? "The operation failed. See the server log." : error.Message });
        }
    }

    private static int? Map(Exception error) => error switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        BadHttpRequestException => StatusCodes.Status400BadRequest,
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException or MissingWorkflowException => StatusCodes.Status409Conflict,
        OperationCanceledException => null,
        _ => StatusCodes.Status500InternalServerError,
    };
}
