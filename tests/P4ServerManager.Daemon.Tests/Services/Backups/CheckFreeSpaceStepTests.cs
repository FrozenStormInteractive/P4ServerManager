using P4ServerManager.Backup.Manifests;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

/// <summary>
/// The check of the free space before a restore touches anything, with a fake disk: the volumes are only named.
/// </summary>
public sealed class CheckFreeSpaceStepTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly string ServerDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "p4sm-space", "main"));

    private static readonly string RootPath = Path.Combine(ServerDirectory, "root");

    private readonly IVolumeSpaceReader _volumeSpaceReader = Substitute.For<IVolumeSpaceReader>();
    private readonly CheckFreeSpaceStep _step;

    public CheckFreeSpaceStepTests()
    {
        _volumeSpaceReader.GetVolume(Arg.Any<string>()).Returns("/volume1");
        _step = new CheckFreeSpaceStep(_volumeSpaceReader);
    }

    [Fact]
    public async Task ANewInstance_NeedsEveryFileOfThePoint_InItsServerDirectory()
    {
        _volumeSpaceReader.GetAvailableFreeSpace("/volume1").Returns(15 * GiB);

        string? summary = await RunAsync(RestoreTarget.NewInstance);

        summary.ShouldBe("13 GiB needed at least, 15 GiB free on /volume1");
        _volumeSpaceReader.Received().GetVolume(ServerDirectory);
    }

    [Fact]
    public async Task ANewInstance_IsRefused_WhenThePointDoesNotFit()
    {
        _volumeSpaceReader.GetAvailableFreeSpace("/volume1").Returns(12 * GiB);

        InstanceOperationException exception = await Should.ThrowAsync<InstanceOperationException>(
            () => RunAsync(RestoreTarget.NewInstance));

        exception.Message.ShouldBe("/volume1 has 12 GiB free, and the restore needs at least 13 GiB there.");
    }

    [Fact]
    public async Task ARestoreOverAnInstance_NeedsOnlyTheCheckpointAndTheJournals_NextToItsRoot()
    {
        // The archives may come back from the data the instance already has.
        _volumeSpaceReader.GetAvailableFreeSpace("/volume1").Returns(4 * GiB);

        string? summary = await RunAsync(RestoreTarget.ExistingInstance);

        summary.ShouldBe("3 GiB needed at least, 4 GiB free on /volume1");
        _volumeSpaceReader.Received().GetVolume(RootPath);
    }

    [Fact]
    public async Task ARestoreOverAnInstance_IsRefused_WhenTheCheckpointAndTheJournalsDoNotFit()
    {
        _volumeSpaceReader.GetAvailableFreeSpace("/volume1").Returns(2 * GiB);

        InstanceOperationException exception = await Should.ThrowAsync<InstanceOperationException>(
            () => RunAsync(RestoreTarget.ExistingInstance));

        exception.Message.ShouldBe("/volume1 has 2 GiB free, and the restore needs at least 3 GiB there.");
    }

    [Fact]
    public async Task GoesOn_WhenTheFreeSpaceCannotBeRead()
    {
        _volumeSpaceReader.GetAvailableFreeSpace("/volume1").Returns((long?)null);

        string? summary = await RunAsync(RestoreTarget.NewInstance);

        summary.ShouldBe("The free space of /volume1 cannot be read: checked once the files are placed");
    }

    /// <summary>
    /// Runs the step on a point of 13 GiB: a checkpoint of 2 GiB, a journal of 1 GiB and 10 GiB of archives.
    /// </summary>
    private Task<string?> RunAsync(RestoreTarget target)
    {
        P4ServerInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Name = "main",
            Port = 1666,
            RootPath = RootPath,
            Release = "r25.1",
        };
        RestoreRun run = new()
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            InstanceName = instance.Name,
            Target = target,
            DestinationName = "nas",
            SnapshotKey = "snapshots/key",
        };
        BackupDestination destination = new()
        {
            Id = Guid.NewGuid(),
            Name = "nas",
            Settings = "{}",
            ProtectedPassphrase = string.Empty,
        };
        RestoreContext context = new(null!, instance, run, destination)
        {
            Manifest = new BackupManifest(
                new BackupManifestHeader
                {
                    Id = Guid.NewGuid(),
                    SourceId = Guid.NewGuid(),
                    SourceName = "main",
                    Kind = "full",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                },
                [
                    Entry(BackupFileRoles.Checkpoint, "journals/checkpoint.12", 2 * GiB),
                    Entry(BackupFileRoles.Journal, "journals/journal.12", GiB),
                    Entry(BackupFileRoles.Archive, "archives/depot/a.txt,v", 6 * GiB),
                    Entry(BackupFileRoles.Archive, "archives/depot/b.bin,d/1.1.gz", 4 * GiB),
                ]),
        };
        return _step.RunAsync(context, new Progress<OperationStepProgress>(), TestContext.Current.CancellationToken);
    }

    private static BackupManifestEntry Entry(string role, string path, long size)
    {
        return new BackupManifestEntry(role, path, size, DateTimeOffset.UnixEpoch, new string('a', 64), "object");
    }
}
