using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace P4ServerManager.Daemon.Filters;

/// <summary>
/// Answers a 502 when the Perforce FTP, the only remote server the daemon calls, cannot be read: while listing the
/// p4d releases, or while a validator checks a release against them.
/// </summary>
public sealed class PerforceFtpExceptionFilter(ProblemDetailsFactory problemDetailsFactory) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not HttpRequestException)
        {
            return;
        }

        ProblemDetails problem = problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            StatusCodes.Status502BadGateway,
            detail: "The list of p4d releases could not be read from the Perforce FTP.");
        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }
}
