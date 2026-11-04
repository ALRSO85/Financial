using Financial.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Financial.Api;

public sealed class FinancialExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<FinancialExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested) return false;

        var statusCode = exception is FinancialApplicationException applicationException
            ? applicationException.StatusCode
            : StatusCodes.Status500InternalServerError;
        if (statusCode >= 500)
            logger.LogError("Financial request failed with {ExceptionType}", exception.GetType().Name);
        else
            logger.LogWarning("Financial request rejected with {StatusCode}", statusCode);

        httpContext.Response.StatusCode = statusCode;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = ReasonPhrases.GetReasonPhrase(statusCode),
                Detail = exception is FinancialApplicationException safeException
                    ? safeException.Message
                    : "Ocorreu uma falha inesperada. Use o identificador de correlação para diagnóstico.",
                Instance = httpContext.Request.Path
            }
        });

        return true;
    }
}
