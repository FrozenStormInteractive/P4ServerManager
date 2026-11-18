using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances.Deletion;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

/// <summary>
/// The plan of the deletion of the files of an instance, on folders laid out on disk.
/// </summary>
public sealed class InstanceFilePlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));

    private string Servers => Path.Combine(_directory, "share");

    private string Data => Path.Combine(_directory, "data");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Plan_TakesTheWholeServerDirectoryOfTheDefaultLayout()
    {
        string server = LayOutDefaultServer("main");

        InstanceFilePlan plan = Plan(server, DefaultDescription("main"));

        plan.LayoutRead.ShouldBeTrue();
        plan.Problem.ShouldBeNull();
        plan.Directories.ShouldBe([Path.Combine(server, "archives"), Path.Combine(server, "root")], ignoreOrder: true);
        plan.EmptiedDirectories.ShouldContain(server);
        plan.EmptiedDirectories.ShouldContain(Path.Combine(server, "journals"));
        plan.EmptiedDirectories.ShouldContain(Path.Combine(server, "logs"));

        InstanceFilePlanner.Delete(plan, TestContext.Current.CancellationToken);

        Directory.Exists(server).ShouldBeFalse();
        Directory.Exists(Servers).ShouldBeTrue();
    }

    [Fact]
    public void Plan_KeepsWhatTheServerDoesNotUse()
    {
        string server = LayOutDefaultServer("main");
        string notes = Path.Combine(server, "journals", "notes.txt");
        File.WriteAllText(notes, "mine");
        string otherJournal = Path.Combine(server, "journals", "other.ckp.3.gz");
        File.WriteAllText(otherJournal, "another server");

        InstanceFilePlan plan = Plan(server, DefaultDescription("main"));
        InstanceFilePlanner.Delete(plan, TestContext.Current.CancellationToken);

        plan.EmptiedDirectories.ShouldNotContain(server);
        plan.EmptiedDirectories.ShouldNotContain(Path.Combine(server, "journals"));
        File.Exists(notes).ShouldBeTrue();
        File.Exists(otherJournal).ShouldBeTrue();
        File.Exists(Path.Combine(server, "journals", "main.ckp.1.gz")).ShouldBeFalse();
        File.Exists(Path.Combine(server, "journals", "journal")).ShouldBeFalse();
        Directory.Exists(Path.Combine(server, "root")).ShouldBeFalse();
        Directory.Exists(Path.Combine(server, "logs")).ShouldBeFalse();
    }

    [Fact]
    public void Plan_ListsTheJournalsAndLogsOfTheServerOneByOne()
    {
        string server = LayOutDefaultServer("main");

        InstanceFilePlan plan = Plan(server, DefaultDescription("main"));

        plan.Files.ShouldBe(
            [
                Path.Combine(server, "journals", "journal"),
                Path.Combine(server, "journals", "main.ckp.1.gz"),
                Path.Combine(server, "journals", "main.jnl.0"),
                Path.Combine(server, "logs", "errors.1.csv"),
                Path.Combine(server, "logs", "errors.csv"),
                Path.Combine(server, "logs", "log"),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void DeleteTree_StopsWhenTheDaemonStops()
    {
        string folder = Path.Combine(_directory, "large");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 1000; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"file{i}"), "data");
        }
        using CancellationTokenSource stopping = new();
        stopping.Cancel();

        Should.Throw<OperationCanceledException>(() => InstanceFilePlanner.DeleteTree(folder, stopping.Token));

        Directory.Exists(folder).ShouldBeTrue();
        InstanceFilePlanner.DeleteTree(folder, TestContext.Current.CancellationToken);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void DeleteTree_DeletesALinkButNotWhatItPointsTo()
    {
        string target = Path.Combine(_directory, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "kept.txt"), "kept");
        string folder = Path.Combine(_directory, "folder");
        Directory.CreateDirectory(folder);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs a right this account lacks.");
        }

        InstanceFilePlanner.DeleteTree(folder, TestContext.Current.CancellationToken);

        Directory.Exists(folder).ShouldBeFalse();
        File.Exists(Path.Combine(target, "kept.txt")).ShouldBeTrue();
    }

    [Fact]
    public void Plan_TakesADepotKeptElsewhere()
    {
        string server = LayOutDefaultServer("main");
        string elsewhere = Path.Combine(_directory, "big-disk", "assets");
        Directory.CreateDirectory(elsewhere);
        ServerDescription description = DefaultDescription("main") with
        {
            Depots = [new DepotRecord("depot", 0, "depot/..."), new DepotRecord("assets", 0, elsewhere + "/...")],
        };

        InstanceFilePlan plan = Plan(server, description);

        plan.Directories.ShouldContain(elsewhere);
    }

    [Fact]
    public void Plan_WithoutTheConfiguration_TakesTheRootAndTheUsualFolders()
    {
        string server = LayOutDefaultServer("main");

        InstanceFilePlan plan = Plan(server, description: null);

        plan.LayoutRead.ShouldBeFalse();
        plan.Directories.ShouldBe(
            [
                Path.Combine(server, "archives"),
                Path.Combine(server, "journals"),
                Path.Combine(server, "logs"),
                Path.Combine(server, "root"),
            ],
            ignoreOrder: true);
        plan.EmptiedDirectories.ShouldBe([server]);
    }

    [Fact]
    public void Plan_NeverEmptiesTheServersDirectory()
    {
        // An imported root right in the servers directory: its parent is the servers directory itself.
        Directory.CreateDirectory(Path.Combine(Servers, "root"));
        Directory.CreateDirectory(Data);

        InstanceFilePlan plan = Plan(Servers, description: null);
        InstanceFilePlanner.Delete(plan, TestContext.Current.CancellationToken);

        plan.Problem.ShouldBeNull();
        plan.EmptiedDirectories.ShouldBeEmpty();
        Directory.Exists(Path.Combine(Servers, "root")).ShouldBeFalse();
        Directory.Exists(Servers).ShouldBeTrue();
    }

    [Fact]
    public void Plan_RefusesAFolderThatHoldsTheRootOfAnotherInstance()
    {
        string server = LayOutDefaultServer("main");
        string otherRoot = Path.Combine(server, "archives", "nested", "root");
        Directory.CreateDirectory(otherRoot);

        InstanceFilePlan plan = Plan(server, DefaultDescription("main"), otherRoots: [otherRoot]);

        plan.Problem.ShouldNotBeNull().ShouldContain("of another instance");
    }

    [Fact]
    public void Plan_RefusesAFolderThatHoldsTheFilesOfTheDaemon()
    {
        string server = LayOutDefaultServer("main");
        // A depot that keeps its files in the data directory of the daemon.
        ServerDescription description = DefaultDescription("main") with
        {
            Depots = [new DepotRecord("depot", 0, "depot/..."), new DepotRecord("odd", 0, Data + "/...")],
        };

        InstanceFilePlan plan = Plan(server, description);

        plan.Problem.ShouldNotBeNull().ShouldContain("where the daemon keeps its own files");
    }

    [Fact]
    public void Plan_RefusesARootOfTheFileSystem()
    {
        string root = Path.GetPathRoot(_directory)!;

        InstanceFilePlan plan = InstanceFilePlanner.Plan(root, null, Path.Combine(_directory, "log"), [], [], []);

        plan.Problem.ShouldNotBeNull().ShouldContain("a root of the file system");
    }

    // A server laid out as the daemon creates it, with a checkpoint, a rotated journal, the live journal and logs.
    private string LayOutDefaultServer(string name)
    {
        string server = Path.Combine(Servers, name);
        Directory.CreateDirectory(Path.Combine(server, "root"));
        File.WriteAllText(Path.Combine(server, "root", "db.counters"), "data");
        Directory.CreateDirectory(Path.Combine(server, "archives", "depot"));
        File.WriteAllText(Path.Combine(server, "archives", "depot", "file,v"), "archive");
        Directory.CreateDirectory(Path.Combine(server, "journals"));
        File.WriteAllText(Path.Combine(server, "journals", $"{name}.ckp.1.gz"), "checkpoint");
        File.WriteAllText(Path.Combine(server, "journals", $"{name}.jnl.0"), "journal");
        File.WriteAllText(Path.Combine(server, "journals", "journal"), "live journal");
        Directory.CreateDirectory(Path.Combine(server, "logs"));
        File.WriteAllText(Path.Combine(server, "logs", "log"), "log");
        File.WriteAllText(Path.Combine(server, "logs", "errors.csv"), "errors");
        File.WriteAllText(Path.Combine(server, "logs", "errors.1.csv"), "rotated errors");
        Directory.CreateDirectory(Data);
        return server;
    }

    private static ServerDescription DefaultDescription(string name)
    {
        return new ServerDescription(
            new Dictionary<string, string>
            {
                ["journalPrefix"] = $"../journals/{name}",
                ["P4JOURNAL"] = "../journals/journal",
                ["server.depot.root"] = "../archives",
                ["serverlog.file.1"] = "../logs/errors.csv",
            },
            [new DepotRecord("depot", 0, "depot/...")]);
    }

    private InstanceFilePlan Plan(string server, ServerDescription? description, IReadOnlyList<string>? otherRoots = null)
    {
        return InstanceFilePlanner.Plan(
            Path.Combine(server, "root"),
            description,
            Path.Combine(server, "logs", "log"),
            [Path.Combine(Data, "instances", "abc")],
            otherRoots ?? [],
            [Data, Servers]);
    }
}
