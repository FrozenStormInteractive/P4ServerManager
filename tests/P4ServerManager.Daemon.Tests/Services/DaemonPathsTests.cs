using P4ServerManager.Daemon.Services;

namespace P4ServerManager.Daemon.Tests.Services;

public sealed class DaemonPathsTests
{
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "p4sm-paths", "data");

    [Fact]
    public void Defaults_KeepEverythingInTheDataDirectory()
    {
        DaemonPaths paths = new(Data);
        Guid id = Guid.NewGuid();

        paths.CacheDirectory.ShouldBe(Data);
        paths.RuntimeDirectory.ShouldBe(Data);
        paths.ServersDirectory.ShouldBe(Path.Combine(Data, "servers"));
        paths.SocketPath.ShouldBe(Path.Combine(Data, "daemon.sock"));
        paths.BuildsDirectory.ShouldBe(Path.Combine(Data, "builds"));
        paths.GetPidFilePath(id).ShouldBe(Path.Combine(Data, "instances", id.ToString("N"), "p4d.pid"));
        paths.GetDefaultRootPath("Main").ShouldBe(Path.Combine(Data, "servers", "Main", "root"));
    }

    [Fact]
    public void EachKindOfFile_GoesToItsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "p4sm-paths");
        string cache = Path.Combine(root, "cache");
        string runtime = Path.Combine(root, "run");
        string servers = Path.Combine(root, "share");
        DaemonPaths paths = new(Data, "daemon.sock", null, cache, runtime, servers);
        Guid id = Guid.NewGuid();

        paths.DatabasePath.ShouldBe(Path.Combine(Data, "p4servermanager.db"));
        paths.KeysDirectory.ShouldBe(Path.Combine(Data, "keys"));
        paths.ArchiveDirectory.ShouldBe(Path.Combine(Data, "archive"));
        paths.GetClientStateDirectory(id).ShouldBe(Path.Combine(Data, "instances", id.ToString("N"), "client"));
        paths.GetRestoreStatePath(id).ShouldStartWith(Path.Combine(Data, "instances", id.ToString("N")));
        paths.BuildsDirectory.ShouldBe(Path.Combine(cache, "builds"));
        paths.SocketPath.ShouldBe(Path.Combine(runtime, "daemon.sock"));
        paths.GetPidFilePath(id).ShouldBe(Path.Combine(runtime, "instances", id.ToString("N"), "p4d.pid"));
        paths.GetStartOutputPath(id).ShouldStartWith(Path.Combine(runtime, "instances", id.ToString("N")));
        paths.BackupTemporaryDirectory.ShouldStartWith(runtime);
        paths.GetDefaultServerDirectory("Main").ShouldBe(Path.Combine(servers, "Main"));
    }

    [Fact]
    public void RelativeFolders_AreResolvedAgainstTheDataDirectory()
    {
        DaemonPaths paths = new(Data, null, null, "cache", "run", "../share");

        paths.CacheDirectory.ShouldBe(Path.Combine(Data, "cache"));
        paths.RuntimeDirectory.ShouldBe(Path.Combine(Data, "run"));
        paths.ServersDirectory.ShouldBe(Path.Combine(Path.GetDirectoryName(Data)!, "share"));
        paths.SocketPath.ShouldBeNull();
    }
}
