using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;

namespace P4ServerManager.Cli.Tests.Support;

/// <summary>
/// Answers the requests of the CLI as the daemon would, from responses set by the test, and records them.
/// </summary>
internal sealed class StubDaemon : HttpMessageHandler
{
    private readonly Dictionary<(HttpMethod Method, string PathAndQuery), Queue<Func<HttpResponseMessage>>> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// Answers a request with the responses in turn. The last one answers every later request.
    /// </summary>
    public StubDaemon On(HttpMethod method, string pathAndQuery, params Func<HttpResponseMessage>[] responses)
    {
        _routes[(method, pathAndQuery)] = new Queue<Func<HttpResponseMessage>>(responses);
        return this;
    }

    public IEnumerable<RecordedRequest> RequestsTo(HttpMethod method, string pathAndQuery)
    {
        return Requests.Where(r => r.Method == method && r.PathAndQuery == pathAndQuery);
    }

    public static Func<HttpResponseMessage> Json<T>(HttpStatusCode status, T value, JsonTypeInfo<T> type)
    {
        return () => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json"),
        };
    }

    public static Func<HttpResponseMessage> Problem(
        HttpStatusCode status,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        ApiProblem problem = new(status.ToString(), detail, (int)status, errors);
        return () => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(problem, CliJsonContext.Default.ApiProblem),
                Encoding.UTF8,
                "application/problem+json"),
        };
    }

    public static Func<HttpResponseMessage> Status(HttpStatusCode status)
    {
        return () => new HttpResponseMessage(status);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string pathAndQuery = request.RequestUri!.PathAndQuery;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, pathAndQuery, body));

        if (!_routes.TryGetValue((request.Method, pathAndQuery), out Queue<Func<HttpResponseMessage>>? responses))
        {
            throw new InvalidOperationException($"The test sets no response for {request.Method} {pathAndQuery}.");
        }
        return responses.Count > 1 ? responses.Dequeue()() : responses.Peek()();
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body);
