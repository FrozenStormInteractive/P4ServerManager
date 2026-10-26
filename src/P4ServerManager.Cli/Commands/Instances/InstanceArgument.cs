using System.CommandLine;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class InstanceArgument
{
    public static Argument<string> Create()
    {
        return new Argument<string>("instance") { Description = "Name or identifier of the instance." };
    }
}
