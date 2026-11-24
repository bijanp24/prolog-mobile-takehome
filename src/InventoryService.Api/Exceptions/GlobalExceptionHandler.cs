using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryService.Api.Exceptions;

/// <summary>
/// Last line of defence: turns any exception into an RFC 7807 ProblemDetails response,
/// so callers never see a stack trace. Upstream exceptions map to 502/503; anything
/// else is our bug and maps to a generic 500.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The caller hung up: nobody is listening, so there is nothing useful to write.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request was cancelled by the caller.");
            return true;
        }

        int statusCode;
        string title;
        string detail;

        switch (exception)
        {
            case UpstreamUnavailableException:
                statusCode = StatusCodes.Status503ServiceUnavailable;
                title = "Inventory API is unavailable";
                detail = exception.Message;
                break;
            case UpstreamRejectedException:
                statusCode = StatusCodes.Status502BadGateway;
                title = "Inventory API rejected the request";
                detail = exception.Message;
                break;
            case UpstreamDataException:
                statusCode = StatusCodes.Status502BadGateway;
                title = "Inventory API returned incomplete or inconsistent data";
                detail = exception.Message;
                break;
            default:
                statusCode = StatusCodes.Status500InternalServerError;
                title = "Unexpected error";
                detail = "An unexpected error occurred. See the server logs for details.";
                break;
        }

        _logger.LogError(exception, "Request failed: {Title}. Returning {StatusCode}.", title, statusCode);

        httpContext.Response.StatusCode = statusCode;
        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            }
        });
    }
}
