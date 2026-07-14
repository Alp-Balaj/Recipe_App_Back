using Microsoft.AspNetCore.Diagnostics;

namespace RecipeApp.API;

// Global catch-all for unhandled exceptions. Logs the exception server-side (so failures
// stop being invisible) and returns an RFC-7807 ProblemDetails 500 with NO stack trace in
// the body — safe to run in Production. Registered via AddExceptionHandler<T> +
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

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // TryWriteAsync emits an RFC-7807 body via the registered ProblemDetails service.
        // Only the status/title/type are set — the exception message and stack trace are
        // deliberately never copied into the response.
        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            },
        });
    }
}
