using P4ServerManager.Daemon.Services.Backups;
using P4ServerManager.Daemon.Services.Backups.Restore;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

public sealed class VolumeSpaceReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-volume-tests", Guid.NewGuid().ToString("N"));

    private string SystemRoot => Path.GetPathRoot(_directory)!;

    private string Volume => Path.Combine(_directory, "volume1");

    public VolumeSpaceReaderTests()
    {
        Directory.CreateDirectory(Volume);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            RestoreSetAside.DeleteDirectory(_directory);
        }
    }

    [Fact]
    public void FindMountPoint_ChoosesTheDeepestMountPointThatHoldsThePath()
    {
        string share = Path.Combine(Volume, "share");
        string archives = Path.Combine(share, "archives");
        Directory.CreateDirectory(archives);

        VolumeSpaceReader.FindMountPoint(archives, [SystemRoot, Volume, share]).ShouldBe(share);
        VolumeSpaceReader.FindMountPoint(Volume, [SystemRoot, Volume, share]).ShouldBe(Volume);
    }

    [Fact]
    public void FindMountPoint_IsTheOneOfTheRoot_ForAPathOnNoOtherMountPoint()
    {
        string elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        VolumeSpaceReader.FindMountPoint(elsewhere, [SystemRoot, Volume]).ShouldBe(SystemRoot);
    }

    [Fact]
    public void FindMountPoint_ReadsTheNearestExistingFolder_OfAFolderNotCreatedYet()
    {
        string missing = Path.Combine(Volume, "server", "archives", "depot");

        VolumeSpaceReader.FindMountPoint(missing, [SystemRoot, Volume]).ShouldBe(Volume);
    }

    [Fact]
    public void FindMountPoint_DoesNotTakeAFolderWhoseNameOnlyStartsLikeTheMountPoint()
    {
        string sibling = Path.Combine(_directory, "volume10");
        Directory.CreateDirectory(sibling);

        VolumeSpaceReader.FindMountPoint(sibling, [Volume]).ShouldBeNull();
    }

    [Fact]
    public void FindMountPoint_FollowsTheLinksOnThePath()
    {
        string target = Path.Combine(Volume, "p4");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_directory, "p4");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine cannot create a symbolic link: {exception.Message}");
        }

        VolumeSpaceReader.FindMountPoint(Path.Combine(link, "server", "root"), [SystemRoot, Volume]).ShouldBe(Volume);
    }
}
