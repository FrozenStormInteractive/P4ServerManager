using System.CommandLine;
using System.Text.Json.Serialization.Metadata;

using P4ServerManager.Cli.Api;

namespace P4ServerManager.Cli;

/// <summary>
/// One run of a command: its parsed arguments, where it writes, and how it reaches the daemon.
/// </summary>
internal sealed class CliInvocation(ParseResult parseResult, CliEnvironment environment, GlobalOptions globalOptions)
{
    private static readonly Uri BaseAddress = new("http://localhost/");

    public ParseResult ParseResult { get; } = parseResult;

    public CliEnvironment Environment { get; } = environment;

    public TextWriter Output => ParseResult.InvocationConfiguration.Output;

    public TextWriter Error => ParseResult.InvocationConfiguration.Error;

    /// <summary>
    /// True when the result is written as JSON.
    /// </summary>
    public bool Json => ParseResult.GetValue(globalOptions.Json);

    /// <summary>
    /// Where the progress of an operation goes: next to the result, unless the standard output is kept for the JSON.
    /// </summary>
    public TextWriter Progress => Json ? Error : Output;

    public string SocketPath =>
        ParseResult.GetValue(globalOptions.Socket) is { Length: > 0 } option
            ? option
            : Environment.GetEnvironmentVariable(GlobalOptions.SocketVariable) is { Length: > 0 } variable
                ? variable
                : GlobalOptions.DefaultSocketPath;

    public T GetValue<T>(Option<T> option)
    {
        return ParseResult.GetValue(option)!;
    }

    public T GetValue<T>(Argument<T> argument)
    {
        return ParseResult.GetValue(argument)!;
    }

    public DaemonClient CreateClient()
    {
        string socketPath = SocketPath;
        HttpClient httpClient = new(Environment.CreateHandler(socketPath))
        {
            BaseAddress = BaseAddress,
            // The start and the stop of p4d are answered once done, which can take a while.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new DaemonClient(httpClient, socketPath);
    }

    public void WriteJson<T>(T value, JsonTypeInfo<T> type)
    {
        Output.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, type));
    }
}
