using System.CommandLine;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Support;

/// <summary>
/// Runs the command tree of the CLI against a <see cref="StubDaemon"/>, and captures what it writes.
/// </summary>
internal sealed class CliRunner
{
    public StubDaemon Daemon { get; } = new();

    public FakePrompter Prompter { get; } = new();

    /// <summary>
    /// Standard input of the CLI.
    /// </summary>
    public string Input { get; set; } = string.Empty;

    public Dictionary<string, string> EnvironmentVariables { get; } = [];

    /// <summary>
    /// Paths of the sockets the commands connected to.
    /// </summary>
    public List<string> SocketPaths { get; } = [];

    public async Task<CliResult> RunAsync(params string[] args)
    {
        CliEnvironment environment = new()
        {
            CreateHandler = socketPath =>
            {
                SocketPaths.Add(socketPath);
                return Daemon;
            },
            Prompter = Prompter,
            Input = new StringReader(Input),
            GetEnvironmentVariable = name => EnvironmentVariables.GetValueOrDefault(name),
            PollInterval = TimeSpan.Zero,
        };
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliApp.BuildRootCommand(environment)
            .Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    public static InstanceDto Instance(
        string name,
        P4ServerInstanceState state = P4ServerInstanceState.Ready,
        InstanceRuntimeStatus runtimeStatus = InstanceRuntimeStatus.Running,
        int port = 1666,
        Guid? id = null,
        bool maintenanceMode = false,
        SuperUserDto? superUser = null)
    {
        return new InstanceDto(
            id ?? Guid.NewGuid(),
            name,
            Description: null,
            port,
            Ssl: false,
            SslFingerprint: null,
            RootPath: $"/data/instances/{name}/root",
            Release: "r25.1",
            Version: state == P4ServerInstanceState.Provisioning ? null : "2025.1",
            Build: state == P4ServerInstanceState.Provisioning ? null : "2850123",
            CaseSensitive: true,
            Unicode: false,
            AutoStart: true,
            maintenanceMode,
            state,
            runtimeStatus,
            LastError: null,
            superUser,
            CreatedAt: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    }
}

internal sealed record CliResult(int ExitCode, string Output, string Error)
{
    public string[] OutputLines => Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
