using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

public sealed class ServerFileLocatorTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "server", "root"));

    [Fact]
    public void Parse_ReadsQuotedAndBareFields_WithEscapedAtSignsAndLineBreaks()
    {
        IReadOnlyList<IReadOnlyList<string>> records = CheckpointRecordParser.Parse(
            "@pv@ 9 @db.depot@ @depot@ 0 @@ @depot/...@ @a@@b@ \n@pv@ 1 @db.desc@ 4 @two\nlines@ \n");

        records.Count.ShouldBe(2);
        records[0].ShouldBe(["pv", "9", "db.depot", "depot", "0", "", "depot/...", "a@b"]);
        records[1].ShouldBe(["pv", "1", "db.desc", "4", "two\nlines"]);
    }

    [Fact]
    public void ReadConfigurables_PrefersTheValuesOfTheServer()
    {
        const string dump = "@pv@ 3 @db.config@ @any@ @journalPrefix@ @../journals/main@ \n"
            + "@pv@ 3 @db.config@ @main@ @journalPrefix@ @/backup/main@ \n"
            + "@pv@ 3 @db.config@ @edge@ @server.depot.root@ @/edge@ \n";

        IReadOnlyDictionary<string, string> configurables = CheckpointRecordParser.ReadConfigurables(dump, "main");

        configurables["journalPrefix"].ShouldBe("/backup/main");
        configurables.ContainsKey("server.depot.root").ShouldBeFalse();
    }

    [Fact]
    public void ReadDepots_KeepsOnlyTheDepotsThatKeepFiles()
    {
        const string dump = "@pv@ 9 @db.depot@ @depot@ 0 @@ @depot/...@ @@ \n"
            + "@pv@ 9 @db.depot@ @remote@ 1 @@ @//...@ @@ \n"
            + "@pv@ 9 @db.depot@ @spec@ 2 @.p4s@ @spec/...@ @@ \n"
            + "@pv@ 9 @db.depot@ @graph@ 7 @@ @graph/...@ @@ \n";

        CheckpointRecordParser.ReadDepots(dump).Select(d => d.Name).ShouldBe(["depot", "spec"]);
    }

    [Fact]
    public void Resolve_PlacesTheJournalsAndTheDepotsAsTheConfigurablesSay()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>
            {
                ["journalPrefix"] = "../journals/main",
                ["server.depot.root"] = "../archives",
            },
            [new DepotRecord("depot", 0, "depot/..."), new DepotRecord("abs", 0, Path.Combine(Root, "..", "elsewhere") + "/...")]);

        string server = Path.GetDirectoryName(Root)!;
        layout.JournalDirectory.ShouldBe(Path.Combine(server, "journals"));
        layout.JournalPrefix.ShouldBe("main");
        layout.Depots.ShouldBe(
        [
            new DepotLocation("depot", Path.Combine(server, "archives", "depot")),
            new DepotLocation("abs", Path.Combine(server, "elsewhere")),
        ]);
    }

    [Fact]
    public void Resolve_KeepsEverythingInTheRoot_WithoutConfigurables()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>(),
            [new DepotRecord("depot", 0, "depot/...")]);

        layout.JournalDirectory.ShouldBe(Root);
        layout.JournalPrefix.ShouldBeNull();
        layout.Depots.ShouldHaveSingleItem().Directory.ShouldBe(Path.Combine(Root, "depot"));
    }

    [Theory]
    [InlineData("main.ckp.12.gz", "main", JournalFileKind.Checkpoint, 12)]
    [InlineData("main.ckp.12.gz.md5", "main", JournalFileKind.Checksum, 12)]
    [InlineData("main.ckp.12.md5", "main", JournalFileKind.Checksum, 12)]
    [InlineData("main.jnl.11", "main", JournalFileKind.Journal, 11)]
    [InlineData("checkpoint.3.gz", null, JournalFileKind.Checkpoint, 3)]
    [InlineData("journal.2", null, JournalFileKind.Journal, 2)]
    public void ParseJournalFileName_ReadsTheKindAndTheNumber(
        string fileName,
        string? prefix,
        JournalFileKind kind,
        int number)
    {
        ServerFileLocator.ParseJournalFileName(fileName, prefix).ShouldBe(new JournalFile(kind, number));
    }

    [Theory]
    [InlineData("other.ckp.12.gz", "main")]
    [InlineData("journal", null)]
    [InlineData("main.ckp.12.gz", null)]
    [InlineData("main.ckp.x.gz", "main")]
    public void ParseJournalFileName_IgnoresOtherFiles(string fileName, string? prefix)
    {
        ServerFileLocator.ParseJournalFileName(fileName, prefix).ShouldBeNull();
    }
}
