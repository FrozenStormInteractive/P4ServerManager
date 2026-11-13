using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

public sealed class ServerFileLocatorTests
{
    private static readonly string Server = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "server"));

    private static readonly string Root = Path.Combine(Server, "root");

    /// <summary>
    /// What p4d 2026.1 printed for <c>p4d -jd - db.depot</c> on a server made by the daemon.
    /// </summary>
    private const string RealDepotDump = """
        Dumping to -...
        @nx@ 2 1794529632 @62@ 2 1 0 0 0 @instances/328f998f063542f9933c2052d268dcf3/root@ @-@ @@ @@ @@
        @nx@ 4 1794529632 @62@ 2 0 -1436852204 0 0 @db.depot@ @@ @@ @@ @@
        @pv@ 2 @db.depot@ @.p4-extensions@ 8 @@ @.p4-extensions/...@ @@
        @pv@ 2 @db.depot@ @.p4-traits@ 9 @@ @.p4-traits/...@ @@
        @pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@
        @pv@ 2 @db.depot@ @repo@ 7 @@ @repo/...@ @@
        @pv@ 2 @db.depot@ @spec@ 2 @.p4s@ @spec/...@ @@
        @pv@ 2 @db.depot@ @unload@ 5 @@ @unload/...@ @@
        @ex@ 63264 1794529632
        @nx@ 3 1794529632 @62@ 0 0 0 0 0 @@ @@ @@ @@ @@
        @ex@ 63264 1794529632
        """;

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
    public void ReadDepots_ReadsEveryDepotOfARealDump()
    {
        IReadOnlyList<DepotRecord> depots = DepotRecord.ReadAll(RealDepotDump);

        depots.ShouldBe(
        [
            new DepotRecord(".p4-extensions", 8, ".p4-extensions/..."),
            new DepotRecord(".p4-traits", 9, ".p4-traits/..."),
            new DepotRecord("depot", 0, "depot/..."),
            new DepotRecord("repo", 7, "repo/..."),
            new DepotRecord("spec", 2, "spec/..."),
            new DepotRecord("unload", 5, "unload/..."),
        ]);
    }

    [Fact]
    public void ReadDepots_SkipsTheTransactionMarkers()
    {
        const string dump = "@mx@ 63264 1794529632\n"
            + "@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@ \n"
            + "@ex@ 63264 1794529632\n";

        DepotRecord.ReadAll(dump).ShouldHaveSingleItem().Name.ShouldBe("depot");
    }

    [Theory]
    [InlineData("@pv@ 3 @db.depot@ @depot@ 0 @@ @depot/...@ @@ @new@ \n", "version 3")]
    [InlineData("@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ \n", "4 columns instead of 5")]
    [InlineData("@pv@ 2 @db.depot@ @depot@ local @@ @depot/...@ @@ \n", "not a number")]
    [InlineData("@pv@ 1 @db.config@ @any@ @monitor@ @1@ \n", "another table")]
    [InlineData("@dv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@ \n", "no row of a dump")]
    public void ReadDepots_RefusesWhatItDoesNotKnowForSure(string dump, string error)
    {
        Should.Throw<InvalidDataException>(() => DepotRecord.ReadAll(dump)).Message.ShouldContain(error);
    }

    [Fact]
    public void TableDumpReader_NamesTheColumnsOfTheVersionOfEachRow()
    {
        TableSchema version1 = new("db.sample", 1, ["name", "size"]);
        TableSchema version2 = new("db.sample", 2, ["name", "size", "owner"]);
        const string dump = "@pv@ 1 @db.sample@ @a@ 12 \n@pv@ 2 @db.sample@ @b@ 34 @bruno@ \n";

        IReadOnlyList<TableRow> rows = TableDumpReader.Read(dump, version1, version2);

        rows.Count.ShouldBe(2);
        rows[0]["name"].ShouldBe("a");
        rows[0].GetInt32("size").ShouldBe(12);
        rows[1]["owner"].ShouldBe("bruno");
        Should.Throw<ArgumentException>(() => rows[0]["owner"]);
    }

    [Fact]
    public void Resolve_LeavesOutTheDepotsInAnObjectStore()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string> { ["server.depot.root"] = "../archives" },
            [new DepotRecord("cloud", 0, Path.Combine(Server, "cloud") + "/...", "s3,bucket:archives")]);

        layout.ObjectStoreDepots.ShouldBe([new ObjectStoreDepot("cloud", "s3,bucket:archives")]);
        layout.ArchiveSources.ShouldBe([Archives(Path.Combine(Server, "archives"))]);
    }

    [Fact]
    public void ConfigShow_PrefersTheValuesOfTheServer()
    {
        const string output = "any: journalPrefix = ../journals/main\n"
            + "main: journalPrefix = /backup/main\n"
            + "edge: server.depot.root = /edge\n"
            + "any: rejectList = P4EXP,version=2014.2\n";

        IReadOnlyDictionary<string, string> configurables = ConfigShowParser.Parse(output, "main");

        configurables["journalPrefix"].ShouldBe("/backup/main");
        configurables["rejectList"].ShouldBe("P4EXP,version=2014.2");
        configurables.ContainsKey("server.depot.root").ShouldBeFalse();
    }

    [Fact]
    public void ConfigShow_AcceptsOtherBlanksBeforeTheValue_AndKeepsTheValueAsIs()
    {
        IReadOnlyDictionary<string, string> configurables = ConfigShowParser.Parse(
            "any:\tjournalPrefix  =  ../journals/main\n",
            null);

        configurables["journalPrefix"].ShouldBe(" ../journals/main");
    }

    [Fact]
    public void ConfigShow_RefusesALineThatIsNotAConfigurable()
    {
        Should.Throw<InvalidDataException>(() => ConfigShowParser.Parse("any: monitor = 1\nsomething else\n", null));
    }

    [Fact]
    public void Resolve_TakesTheDepotRootWhole_WhenItHoldsNeitherTheRootNorTheJournals()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>
            {
                ["journalPrefix"] = "../journals/main",
                ["server.depot.root"] = "../archives",
            },
            [new DepotRecord("depot", 0, "depot/..."), new DepotRecord("remote", DepotRecord.RemoteType, "//...")]);

        layout.JournalDirectory.ShouldBe(Path.Combine(Server, "journals"));
        layout.JournalPrefix.ShouldBe("main");
        layout.DepotRoot.ShouldBe(Path.Combine(Server, "archives"));
        layout.ArchiveSources.ShouldBe([Archives(Path.Combine(Server, "archives"))]);
        layout.ExcludedDirectories.ShouldBeEmpty();
        layout.ObjectStoreDepots.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_LeavesOutTheJournals_WhenTheDepotRootHoldsThem()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>
            {
                ["journalPrefix"] = "../data/journals/main",
                ["server.depot.root"] = "../data",
            },
            [new DepotRecord("depot", 0, "depot/...")]);

        layout.ArchiveSources.ShouldBe([Archives(Path.Combine(Server, "data"))]);
        layout.ExcludedDirectories.ShouldBe([Path.Combine(Server, "data", "journals")]);
    }

    [Fact]
    public void Resolve_TakesTheFolderOfEachDepot_WithoutDepotRoot()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>(),
            [new DepotRecord("depot", 0, "depot/..."), new DepotRecord("spec", 2, "spec/...")]);

        layout.JournalDirectory.ShouldBe(Root);
        layout.JournalPrefix.ShouldBeNull();
        layout.DepotRoot.ShouldBe(Root);
        layout.ArchiveSources.ShouldBe(
        [
            new ArchiveSource(Path.Combine(Root, "depot"), Root, ServerFileLocator.ArchivesPrefix),
            new ArchiveSource(Path.Combine(Root, "spec"), Root, ServerFileLocator.ArchivesPrefix),
        ]);
    }

    [Fact]
    public void Resolve_AddsTheDepotsOutsideTheDepotRoot_UnderTheirName_Once()
    {
        string elsewhere = Path.Combine(Server, "elsewhere");
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string> { ["server.depot.root"] = "../archives" },
            [
                new DepotRecord("depot", 0, "depot/..."),
                new DepotRecord("abs", 0, elsewhere + "/..."),
                new DepotRecord("nested", 0, Path.Combine(elsewhere, "nested") + "/..."),
                new DepotRecord("same", 0, elsewhere + "/..."),
            ]);

        layout.ArchiveSources.ShouldBe(
        [
            Archives(Path.Combine(Server, "archives")),
            new ArchiveSource(elsewhere, elsewhere, "depots/abs"),
        ]);
    }

    [Fact]
    public void Resolve_LeavesOutTheJournals_WhenADepotOutsideTheDepotRootHoldsThem()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>
            {
                ["journalPrefix"] = "../shared/journals/main",
                ["server.depot.root"] = "../archives",
            },
            [new DepotRecord("shared", 0, Path.Combine(Server, "shared") + "/...")]);

        layout.ExcludedDirectories.ShouldBe([Path.Combine(Server, "shared", "journals")]);
    }

    [Fact]
    public void Resolve_RefusesADepotThatHoldsTheRoot()
    {
        Should.Throw<InstanceOperationException>(() => ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string>(),
            [new DepotRecord("all", 0, "...")]))
            .Message.ShouldContain("holds the root");
    }

    private static ArchiveSource Archives(string directory)
    {
        return new ArchiveSource(directory, directory, ServerFileLocator.ArchivesPrefix);
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
