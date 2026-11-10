using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

public class RecommendedConfigurationTests
{
    private static readonly P4ServerInstance Instance = new()
    {
        Name = "main",
        Port = 1666,
        RootPath = "/volume1/p4/main/root",
        Release = "r25.1",
    };

    [Fact]
    public void GetConfigurables_PointsToTheFoldersNextToTheRoot()
    {
        Dictionary<string, string> configurables = RecommendedConfiguration.GetConfigurables(Instance)
            .ToDictionary(c => c.Name, c => c.Value);

        configurables["server.depot.root"].ShouldBe("../archives");
        configurables["journalPrefix"].ShouldBe("../journals/main");
        configurables["serverlog.file.1"].ShouldBe("../logs/commands.csv");
        configurables["serverlog.file.5"].ShouldBe("../logs/auth.csv");
        configurables["serverlog.retain.5"].ShouldBe("10");
        configurables["dm.user.noautocreate"].ShouldBe("2");
        configurables["rejectList"].ShouldBe("P4EXP,version=2014.2");
        configurables.Count.ShouldBe(37);
    }

    [Theory]
    [InlineData(false, "Address:\t1666\n")]
    [InlineData(true, "Address:\tssl:1666\n")]
    public void GetServerSpec_MakesACommitServerWithTheAddressOfTheInstance(bool ssl, string address)
    {
        P4ServerInstance instance = new()
        {
            Name = Instance.Name,
            Port = Instance.Port,
            RootPath = Instance.RootPath,
            Release = Instance.Release,
            Ssl = ssl,
        };

        string spec = RecommendedConfiguration.GetServerSpec(instance, "main");

        spec.ShouldStartWith("ServerID:\tmain\n");
        spec.ShouldContain("Type:\tserver\n");
        spec.ShouldContain("Services:\tcommit-server\n");
        spec.ShouldContain(address);
    }

    [Fact]
    public void GetServerSpec_IsNamedAfterTheGivenServerId()
    {
        P4ServerInstance instance = new()
        {
            Name = Instance.Name,
            Port = Instance.Port,
            RootPath = Instance.RootPath,
            Release = Instance.Release,
        };

        RecommendedConfiguration.GetServerSpec(instance, "edge").ShouldStartWith("ServerID:\tedge\n");
    }

    [Fact]
    public void GetTypeMap_HasOneLinePerEntry()
    {
        string[] lines = RecommendedConfiguration.GetTypeMap().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldBe("TypeMap:");
        lines[1].ShouldBe("\ttext //....asp");
        lines[^1].ShouldBe("\tbinary+l //....sxw");
        lines.Length.ShouldBe(64);
    }

    [Fact]
    public void AddHideSpecDepotLine_HidesTheSpecDepotBeforeTheSuperUserLine()
    {
        const string protections = "# Comment\n\nProtections:\n\twrite user * * //...\n\tsuper user admin * //...\n";

        string result = CreateSuperUserStep.AddHideSpecDepotLine(protections);

        result.ShouldBe(
            "# Comment\n\nProtections:\n\twrite user * * //...\n\tlist user * * -//spec/...\n"
            + "\tsuper user admin * //...\n");
    }

    [Theory]
    [InlineData("Sup3rSecret", true)]
    [InlineData("secret-password", true)]
    [InlineData("ALLUPPER9", true)]
    [InlineData("MixedCase", true)]
    [InlineData("onlylowercase", false)]
    [InlineData("12345678", false)]
    [InlineData("Ab1", false)]
    public void SuperUserPassword_NeedsEightCharactersFromTwoClasses(string password, bool strong)
    {
        SuperUserPassword.IsStrong(password).ShouldBe(strong);
    }
}
