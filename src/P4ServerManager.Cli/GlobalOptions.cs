using System.CommandLine;

namespace P4ServerManager.Cli;

/// <summary>
/// Options every command takes.
/// </summary>
internal sealed class GlobalOptions
{
    public const string SocketVariable = "P4SM_SOCKET";

    /// <summary>
    /// Where the systemd service of the daemon puts its socket.
    /// </summary>
    public const string DefaultSocketPath = "/run/p4servermanager/daemon.sock";

    public Option<string?> Socket { get; } = new("--socket")
    {
        Description = $"Path of the socket of the daemon. Defaults to ${SocketVariable}, then {DefaultSocketPath}.",
        HelpName = "path",
        Recursive = true,
    };

    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Write the result as JSON, for scripts.",
        Recursive = true,
    };
}
