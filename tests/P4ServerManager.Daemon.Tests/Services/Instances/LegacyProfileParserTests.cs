using P4ServerManager.Daemon.Services.Instances.Import;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

public class LegacyProfileParserTests
{
    // An absolute path on the machine that runs the tests: the parser rejects relative roots.
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "perforce", "main", "root");

    [Fact]
    public void Parse_ReadsAProfileOfP4dctlNg()
    {
        string content = $$"""
            {
                "Name": "main",
                "ServerType": "p4d",
                "Owner": "Perforce",
                "Execute": "/var/packages/HelixCoreServer/target/usr/local/bin/p4d",
                "Args": "-C1",
                "Enabled": false,
                "Environment": {
                    "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}},
                    "P4PORT": "1666"
                }
            }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("main.json", content);

        profile.Id.ShouldBe("main.json");
        profile.Name.ShouldBe("main");
        profile.RootPath.ShouldBe(Root);
        profile.Port.ShouldBe(1666);
        profile.CaseSensitive.ShouldBeFalse();
        profile.Enabled.ShouldBeFalse();
        profile.IgnoredSettings.ShouldBeEmpty();
        profile.Problems.ShouldBeEmpty();
        profile.CanImport.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ReadsKeysInAnyCaseAndDefaultsTheNameToTheFileName()
    {
        string content = $$"""
            { "environment": { "p4root": {{System.Text.Json.JsonSerializer.Serialize(Root)}} }, }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("legacy.conf", content);

        profile.Name.ShouldBe("legacy");
        profile.RootPath.ShouldBe(Root);
        profile.Port.ShouldBeNull();
        profile.CaseSensitive.ShouldBeTrue();
        profile.Enabled.ShouldBeTrue();
        profile.CanImport.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ReportsTheSettingsItDoesNotKeep()
    {
        string content = $$"""
            {
                "Args": "-C1 -v server=3",
                "Environment": {
                    "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}},
                    "P4PORT": "1666",
                    "P4JOURNAL": "/volume1/journals/main",
                    "P4NAME": "main"
                }
            }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("main.json", content);

        profile.IgnoredSettings.ShouldBe(["P4JOURNAL", "P4NAME", "Args: -v server=3"]);
        profile.CanImport.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public void Parse_RejectsWhatIsNotAProfile(string content)
    {
        LegacyProfile profile = LegacyProfileParser.Parse("broken.json", content);

        profile.CanImport.ShouldBeFalse();
        profile.Problems.ShouldBe(["The profile is not a p4dctl-ng profile."]);
    }

    [Fact]
    public void Parse_RejectsAProfileWithoutRoot()
    {
        LegacyProfile profile = LegacyProfileParser.Parse("main.json", """{ "Environment": { "P4PORT": "1666" } }""");

        profile.RootPath.ShouldBeNull();
        profile.Problems.ShouldBe(["The profile has no P4ROOT."]);
    }

    [Fact]
    public void Parse_RejectsAnotherServerType()
    {
        string content = $$"""
            { "ServerType": "p4broker", "Environment": { "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}} } }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("broker.json", content);

        profile.Problems.ShouldBe(["Only p4d servers can be imported, not p4broker."]);
    }

    [Fact]
    public void Parse_ReadsAnSslPort()
    {
        string content = $$"""
            { "Environment": { "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}}, "P4PORT": "ssl:1666" } }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("main.json", content);

        profile.Port.ShouldBe(1666);
        profile.Ssl.ShouldBeTrue();
        profile.Problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("ssl/")]
    [InlineData("./ssl")]
    public void Parse_AcceptsTheSslFolderOfTheRoot(string sslDirectory)
    {
        LegacyProfile profile = LegacyProfileParser.Parse("main.json", SslProfile(sslDirectory));

        profile.Ssl.ShouldBeTrue();
        profile.Problems.ShouldBeEmpty();
        profile.IgnoredSettings.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_AcceptsTheAbsoluteSslFolderOfTheRoot()
    {
        LegacyProfile profile = LegacyProfileParser.Parse("main.json", SslProfile(Path.Combine(Root, "ssl")));

        profile.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_RejectsAnotherSslFolder()
    {
        string sslDirectory = Path.Combine(Path.GetTempPath(), "perforce", "main", "ssl");

        LegacyProfile profile = LegacyProfileParser.Parse("main.json", SslProfile(sslDirectory));

        profile.Problems.ShouldBe([
            $"The SSL key and certificate are in {sslDirectory}. Move them to {Path.Combine(Root, "ssl")} before the import.",
        ]);
    }

    [Fact]
    public void Parse_IgnoresTheSslFolderWithoutSsl()
    {
        string content = $$"""
            {
                "Environment": {
                    "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}},
                    "P4PORT": "1666",
                    "P4SSLDIR": "/elsewhere"
                }
            }
            """;

        LegacyProfile profile = LegacyProfileParser.Parse("main.json", content);

        profile.Ssl.ShouldBeFalse();
        profile.Problems.ShouldBeEmpty();
        profile.IgnoredSettings.ShouldBe(["P4SSLDIR"]);
    }

    [Theory]
    [InlineData("1666", 1666, false)]
    [InlineData("tcp:1666", 1666, false)]
    [InlineData("tcp6:[::]:1667", 1667, false)]
    [InlineData("0.0.0.0:1668", 1668, false)]
    [InlineData("ssl:perforce.local:1669", 1669, true)]
    [InlineData("ssl64:1670", 1670, true)]
    [InlineData("tcp:", null, false)]
    [InlineData("99999", null, false)]
    public void ParsePort_ReadsThePortAtTheEnd(string p4Port, int? expectedPort, bool expectedSecure)
    {
        int? port = LegacyProfileParser.ParsePort(p4Port, out bool secure);

        port.ShouldBe(expectedPort);
        secure.ShouldBe(expectedSecure);
    }

    private static string SslProfile(string sslDirectory)
    {
        return $$"""
            {
                "Environment": {
                    "P4ROOT": {{System.Text.Json.JsonSerializer.Serialize(Root)}},
                    "P4PORT": "ssl:1666",
                    "P4SSLDIR": {{System.Text.Json.JsonSerializer.Serialize(sslDirectory)}}
                }
            }
            """;
    }
}
