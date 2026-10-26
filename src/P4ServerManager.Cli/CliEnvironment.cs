using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Prompts;

namespace P4ServerManager.Cli;

/// <summary>
/// What the commands take from the machine, replaced by the tests.
/// </summary>
internal sealed class CliEnvironment
{
    /// <summary>
    /// Creates the handler that sends the requests through the socket at the given path.
    /// </summary>
    public required Func<string, HttpMessageHandler> CreateHandler { get; init; }

    public required IPrompter Prompter { get; init; }

    /// <summary>
    /// Standard input, read by the options that take a secret from it.
    /// </summary>
    public required TextReader Input { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>
    /// True when the standard output is a terminal: progress can then be rewritten in place.
    /// </summary>
    public bool OutputIsTerminal { get; init; }

    /// <summary>
    /// Time between two reads of the progress of an operation.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static CliEnvironment Create()
    {
        return new CliEnvironment
        {
            CreateHandler = UnixSocketHandler.Create,
            Prompter = new ConsolePrompter(),
            Input = Console.In,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            OutputIsTerminal = !Console.IsOutputRedirected,
        };
    }
}
