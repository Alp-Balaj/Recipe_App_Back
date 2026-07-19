using Microsoft.AspNetCore.Diagnostics;

namespace RecipeApp.API;

// Global catch-all for unhandled exceptions. Logs the exception server-side (so failures
// stop being invisible) and returns an RFC-7807 ProblemDetails response with NO stack trace
// in the body — safe to run in Production. Registered via AddExceptionHandler<T> +
// AddProblemDetails and run first in the pipeline (app.UseExceptionHandler()), so it wraps
// authentication, authorization, and every endpoint.
public sealed class GlobalExceptionHandler : IExceptionHandler
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
        _logger.LogError(
            exception,
            "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        // meal-planning cp02 gotcha: a malformed JSON request body (e.g. an unrecognized
        // enum string like DayOfWeek "Fourthday") surfaces as BadHttpRequestException, which
        // already carries the framework's correct 400 in its StatusCode property. Unwrapping
        // it here instead of flattening every exception to 500 preserves that — otherwise
        // this global handler silently turned a well-defined client error into a 500 for any
        // endpoint with an enum (or other strictly-typed) body field.
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        httpContext.Response.StatusCode = statusCode;

        // TryWriteAsync emits an RFC-7807 body via the registered ProblemDetails service.
        // Only the status/title/type are set — the exception message and stack trace are
        // deliberately never copied into the response.
        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = statusCode,
                Title = statusCode == StatusCodes.Status500InternalServerError
                    ? "An unexpected error occurred."
                    : "The request could not be processed.",
            },
        });
    }
}
