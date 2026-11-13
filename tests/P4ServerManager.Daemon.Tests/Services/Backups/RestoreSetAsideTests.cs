using P4ServerManager.Backup.Manifests;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Backups.Restore.Steps;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

public sealed class RestoreSetAsideTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-restore-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string StatePath => Path.Combine(_directory, "instance", "restore-state.json");

    private string Root => Path.Combine(_directory, "server", "root");

    private string Archives => Path.Combine(_directory, "server", "archives");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            RestoreSetAside.DeleteDirectory(_directory);
        }
    }

    [Fact]
    public async Task RollBack_PutsTheFoldersBackAsTheyWere_AndRemovesWhatTheRestoreWrote()
    {
        Write(Path.Combine(Root, "db.rev"), "old database");
        Write(Path.Combine(Archives, "depot", "a.txt,v"), "old archive");
        string journals = Path.Combine(_directory, "server", "journals");
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, Guid.NewGuid(), wasRunning: true, Token);

        await setAside.MoveAsideAsync(Root, Token);
        await setAside.MoveAsideAsync(Archives, Token);
        await setAside.MoveAsideAsync(journals, Token);
        Write(Path.Combine(Root, "db.rev"), "restored database");
        Write(Path.Combine(Archives, "depot", "b.txt,v"), "restored archive");
        Write(Path.Combine(journals, "main.ckp.3.gz"), "restored checkpoint");

        setAside.RollBack();

        File.ReadAllText(Path.Combine(Root, "db.rev")).ShouldBe("old database");
        File.ReadAllText(Path.Combine(Archives, "depot", "a.txt,v")).ShouldBe("old archive");
        File.Exists(Path.Combine(Archives, "depot", "b.txt,v")).ShouldBeFalse();
        Directory.Exists(journals).ShouldBeFalse();
        File.Exists(StatePath).ShouldBeFalse();
        Directory.GetDirectories(Path.Combine(_directory, "server")).Length.ShouldBe(2);
    }

    [Fact]
    public async Task RollBack_MovesTheReusedFilesBack()
    {
        string archive = Path.Combine(Archives, "depot", "a.txt,v");
        Write(archive, "unchanged archive");
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, Guid.NewGuid(), wasRunning: false, Token);
        await setAside.MoveAsideAsync(Archives, Token);
        string previous = setAside.FindPrevious(archive).ShouldNotBeNull();
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        await setAside.ReuseAsync(previous, archive, Token);
        File.Exists(previous).ShouldBeFalse();
        setAside.RollBack();

        File.ReadAllText(archive).ShouldBe("unchanged archive");
    }

    [Fact]
    public async Task RollBack_FromTheStateFile_ResumesAfterAnInterruption()
    {
        Write(Path.Combine(Root, "db.rev"), "old database");
        Guid runId = Guid.NewGuid();
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, runId, wasRunning: true, Token);
        await setAside.MoveAsideAsync(Root, Token);
        Write(Path.Combine(Root, "db.rev"), "restored database");

        RestoreSetAside left = (await RestoreSetAside.LoadAsync(StatePath, Token)).ShouldNotBeNull();
        left.WasRunning.ShouldBeTrue();
        left.RollBack();

        File.ReadAllText(Path.Combine(Root, "db.rev")).ShouldBe("old database");
        (await RestoreSetAside.LoadAsync(StatePath, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task RollBack_KeepsAFolderWhoseMoveWasNotMadeYet()
    {
        Write(Path.Combine(Root, "db.rev"), "old database");
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, Guid.NewGuid(), wasRunning: false, Token);
        await setAside.MoveAsideAsync(Root, Token);
        SetAsideFolder folder = setAside.Folders.ShouldHaveSingleItem();

        // As if the daemon stopped after noting the move and before making it.
        Directory.Move(folder.Aside!, folder.Original);
        (await RestoreSetAside.LoadAsync(StatePath, Token))!.RollBack();

        File.ReadAllText(Path.Combine(Root, "db.rev")).ShouldBe("old database");
    }

    [Fact]
    public async Task Commit_DeletesThePreviousData_ReadOnlyFilesIncluded()
    {
        string archive = Path.Combine(Archives, "depot", "a.txt,v");
        Write(archive, "old archive");
        File.SetAttributes(archive, FileAttributes.ReadOnly);
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, Guid.NewGuid(), wasRunning: false, Token);
        await setAside.MoveAsideAsync(Archives, Token);
        string aside = setAside.Folders.ShouldHaveSingleItem().Aside.ShouldNotBeNull();

        setAside.Commit().ShouldBeEmpty();

        Directory.Exists(aside).ShouldBeFalse();
        File.Exists(StatePath).ShouldBeFalse();
    }

    [Fact]
    public async Task MoveAside_SkipsAFolderInsideOneAlreadyAside()
    {
        Write(Path.Combine(Root, "journals", "main.jnl.1"), "journal");
        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(StatePath, Guid.NewGuid(), wasRunning: false, Token);

        await setAside.MoveAsideAsync(Root, Token);
        await setAside.MoveAsideAsync(Path.Combine(Root, "journals"), Token);

        setAside.Folders.ShouldHaveSingleItem().Original.ShouldBe(Root);
        setAside.Covers(Path.Combine(Root, "journals", "x")).ShouldBeTrue();
    }

    [Fact]
    public void GetReplayOrder_ReplaysTheCheckpoint_ThenTheJournalsRotatedAfterIt()
    {
        BackupManifest manifest = new(
            new BackupManifestHeader
            {
                Id = Guid.NewGuid(),
                SourceId = Guid.NewGuid(),
                SourceName = "main",
                Kind = "incremental",
                CreatedAt = DateTimeOffset.UnixEpoch,
            },
            [
                Entry("journal", "journals/main.jnl.6.gz"),
                Entry("checkpoint", "journals/main.ckp.4.gz"),
                Entry("checksum", "journals/main.ckp.4.md5"),
                Entry("journal", "journals/main.jnl.3.gz"),
                Entry("journal", "journals/main.jnl.4.gz"),
                Entry("archive", "archives/depot/a,v"),
                Entry("journal", "journals/main.jnl.5.gz"),
            ]);

        ReplayCheckpointStep.GetReplayOrder(manifest).Select(r => r.Entry.Path).ShouldBe(
        [
            "journals/main.ckp.4.gz",
            "journals/main.jnl.4.gz",
            "journals/main.jnl.5.gz",
            "journals/main.jnl.6.gz",
        ]);
    }

    private static BackupManifestEntry Entry(string role, string path)
    {
        return new BackupManifestEntry(role, path, 1, DateTimeOffset.UnixEpoch, new string('a', 64), "object");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
