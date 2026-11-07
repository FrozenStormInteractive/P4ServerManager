using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Perforce;

/// <summary>
/// The outputs come from p4d 2026.1, on a database damaged on purpose.
/// </summary>
public sealed class HealthCheckOutputParserTests
{
    [Fact]
    public void ReadOutputLine_FollowsTheTablesOfAValidation()
    {
        HealthCheckOutputParser parser = new();

        parser.ReadOutputLine("Validating db.config").ShouldBeNull();
        parser.ReadOutputLine("Validating db.rev").ShouldBeNull();

        parser.CurrentItem.ShouldBe("db.rev");
        parser.ItemsChecked.ShouldBe(2);
    }

    [Fact]
    public void ReadOutputLine_FollowsThePairsOfACrossCheck()
    {
        HealthCheckOutputParser parser = new();

        parser.ReadOutputLine("Check db.have.pt/db.working...").ShouldBeNull();

        parser.CurrentItem.ShouldBe("db.have.pt/db.working");
        parser.ItemsChecked.ShouldBe(1);
    }

    [Fact]
    public void ReadOutputLine_TiesAnIndentedLineToTheCurrentTable()
    {
        HealthCheckOutputParser parser = new();
        parser.ReadOutputLine("Validating db.rev");

        parser.ReadOutputLine("\t**** Checksum mismatch **** possible data corruption")
            .ShouldBe(new HealthCheckProblem("db.rev", "**** Checksum mismatch **** possible data corruption"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Something else")]
    public void ReadOutputLine_IgnoresAnythingElse(string line)
    {
        new HealthCheckOutputParser().ReadOutputLine(line).ShouldBeNull();
    }

    [Theory]
    [InlineData("db.change/db.desc inconsistencies found.", "db.change/db.desc")]
    [InlineData("Table db.desc not known.", "db.desc")]
    [InlineData("db.desc not found in default/specified server root", "db.desc")]
    [InlineData("Table db.have.pt not known.", "db.have.pt")]
    public void ReadErrorLine_TellsABlockThatNamesATableOnceItIsClosed(string message, string table)
    {
        HealthCheckOutputParser parser = new();

        parser.ReadErrorLine("Perforce server error:").ShouldBeNull();
        parser.ReadErrorLine($"\t{message}").ShouldBeNull();

        parser.ReadErrorLine("Perforce server error:").ShouldBe(new HealthCheckProblem(table, message));
        parser.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Complete_JoinsTheLinesOfTheLastBlock()
    {
        HealthCheckOutputParser parser = new();
        parser.ReadErrorLine("Perforce server error:");
        parser.ReadErrorLine("\tDatabase locking error on db.desc!");
        parser.ReadErrorLine("\tBTree variation is not supported!");

        parser.Complete().ShouldBe(new HealthCheckProblem(
            "db.desc",
            "Database locking error on db.desc! BTree variation is not supported!"));
        parser.Complete().ShouldBeNull();
    }

    [Fact]
    public void Complete_LeavesOutTheSummaryOfAValidation()
    {
        HealthCheckOutputParser parser = new();
        parser.ReadErrorLine("Perforce server error:");
        parser.ReadErrorLine("\tProblems were found in 1 table(s).");

        parser.Complete().ShouldBeNull();
        parser.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Complete_KeepsABlockWithoutATableAsAnError()
    {
        HealthCheckOutputParser parser = new();
        parser.ReadErrorLine("Perforce server error:");
        parser.ReadErrorLine("\tRoot directory (set with $P4ROOT or -r flag) invalid.");
        parser.ReadErrorLine("\tchdir: /nowhere: No such file or directory");

        parser.Complete().ShouldBeNull();
        parser.Errors.ShouldBe(
            ["Root directory (set with $P4ROOT or -r flag) invalid. chdir: /nowhere: No such file or directory"]);
    }
}
