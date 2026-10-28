using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

/// <summary>
/// The <c>protect</c> and <c>unprotect</c> commands, that keep a build out of the retention policy or put it back.
/// </summary>
internal static class ProtectBuildCommands
{
    public static IEnumerable<Command> Create(CliServices services)
    {
        yield return Create(services, "protect", "Keep a build: the retention policy never removes it.", true);
        yield return Create(services, "unprotect", "Let the retention policy remove a build again.", false);
    }

    private static Command Create(CliServices services, string name, string description, bool isProtected)
    {
        Argument<string> buildArgument = BuildArgument.Create();
        Command command = new(name, description) { buildArgument };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BuildDto build = await BuildArgument.ResolveAsync(
                client,
                invocation.GetValue(buildArgument),
                cancellationToken);
            BuildDto updated = await client.UpdateBuildAsync(
                build.Id,
                new UpdateBuildRequest { Protected = isProtected },
                cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(updated, CliJsonContext.Default.BuildDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(isProtected
                    ? $"p4d {BuildFormatting.FormatVersion(updated)} is protected."
                    : $"p4d {BuildFormatting.FormatVersion(updated)} is no longer protected.");
            }
            return 0;
        });
        return command;
    }
}
