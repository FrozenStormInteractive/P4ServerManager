using System.CommandLine;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class InstanceArgument
{
    public static Argument<string> Create()
    {
        return new Argument<string>("instance") { Description = "Name or identifier of the instance." };
    }

    /// <summary>
    /// Any number of instances, left empty when the command takes <c>--all</c> instead.
    /// </summary>
    public static Argument<string[]> CreateMany()
    {
        return new Argument<string[]>("instances")
        {
            Description = "Names or identifiers of the instances.",
            Arity = ArgumentArity.ZeroOrMore,
        };
    }
}
