using Financial.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Financial.Api;

public sealed class FinancialExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not FinancialApplicationException applicationException) return false;

        httpContext.Response.StatusCode = applicationException.StatusCode;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = applicationException.StatusCode,
                Title = ReasonPhrases.GetReasonPhrase(applicationException.StatusCode),
                Detail = applicationException.Message,
                Instance = httpContext.Request.Path
            }
        });

        return true;
    }
}
