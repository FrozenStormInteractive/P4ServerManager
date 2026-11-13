using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

public class ServerConfigurationTests
{
    [Fact]
    public void IsUnicode_ReadsTheConfigurableThatP4dXiSets()
    {
        Read("any: monitor = 1\nany: unicode = 1\n", null).IsUnicode.ShouldBeTrue();
        Read("any: monitor = 1\nmain: security = 3\n", "main").IsUnicode.ShouldBeFalse();
        Read(string.Empty, null).IsUnicode.ShouldBeFalse();
    }

    [Fact]
    public void IsUnicode_IgnoresTheValuesOfAnotherServer()
    {
        Read("edge: unicode = 1\n", "main").IsUnicode.ShouldBeFalse();
        Read("any: unicode = 1\nmain: unicode = 0\n", "main").IsUnicode.ShouldBeFalse();
    }

    private static ServerConfiguration Read(string output, string? serverId)
    {
        return new ServerConfiguration(serverId, ConfigShowParser.Parse(output, serverId));
    }
}
