using System.Net;

namespace P4ServerManager.Cli.Api;

/// <summary>
/// The daemon answered a request with an error status.
/// </summary>
internal sealed class DaemonApiException(HttpStatusCode statusCode, ApiProblem? problem)
    : Exception(problem?.Detail ?? problem?.Title ?? $"The daemon answered {(int)statusCode} {statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public ApiProblem? Problem { get; } = problem;
}
