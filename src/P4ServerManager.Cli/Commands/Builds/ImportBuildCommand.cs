using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

internal static class ImportBuildCommand
{
    public static Command Create(CliServices services)
    {
        Argument<FileInfo?> p4dArgument = new("p4d")
        {
            Description = "The p4d executable for the platform of the daemon.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Argument<FileInfo?> p4Argument = new("p4")
        {
            Description = "The p4 executable of the same release.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<FileInfo?> packageOption = new("--package")
        {
            Description = "The server package of Perforce, helix-core-server.tgz, instead of p4d and p4.",
        };
        Command command = new(
            "import",
            "Import a p4d build into the archive, from p4d and p4 or from the server package of Perforce. The daemon "
            + "reads its version from p4d, which must run on its platform.")
        {
            p4dArgument,
            p4Argument,
            packageOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            FileInfo? p4d = invocation.GetValue(p4dArgument);
            FileInfo? p4 = invocation.GetValue(p4Argument);
            FileInfo? package = invocation.GetValue(packageOption);

            List<(string Field, string Path)> files = (p4d, p4, package) switch
            {
                (null, null, { } archive) => [("package", archive.FullName)],
                ({ } p4dFile, { } p4File, null) => [("p4d", p4dFile.FullName), ("p4", p4File.FullName)],
                _ => throw new CliException("Give p4d and p4, or --package alone."),
            };
            foreach ((_, string path) in files)
            {
                if (!File.Exists(path))
                {
                    throw new CliException($"{path} does not exist.");
                }
            }

            await invocation.Progress.WriteLineAsync("Importing and compressing the build...");
            using DaemonClient client = invocation.CreateClient();
            BuildDto build = await client.ImportBuildAsync(files, cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(build, CliJsonContext.Default.BuildDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(
                    $"Imported p4d {BuildFormatting.FormatVersion(build)} of {build.Release} "
                    + $"({BuildFormatting.FormatSize(build.ArchiveSize)} compressed).");
            }
            return 0;
        });
        return command;
    }
}
