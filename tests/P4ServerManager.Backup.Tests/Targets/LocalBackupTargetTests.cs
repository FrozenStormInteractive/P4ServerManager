using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Tests.Targets;

public sealed class LocalBackupTargetTests : BackupTargetContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p4sm-backup-tests", Guid.NewGuid().ToString("N"));

    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        return Task.FromResult<IBackupTarget?>(new LocalBackupTarget(_root));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task List_LeavesOutTheTemporaryFilesOfAnInterruptedWrite()
    {
        Directory.CreateDirectory(Path.Combine(_root, "objects"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "objects", ".object.1234.partial"), [1], Token);

        List<BackupObjectInfo> objects = [];
        await foreach (BackupObjectInfo info in Target.ListAsync(string.Empty, Token))
        {
            objects.Add(info);
        }

        objects.ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_RefusesARelativeFolder()
    {
        Should.Throw<ArgumentException>(() => new LocalBackupTarget("backups"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a//b")]
    [InlineData(".hidden")]
    [InlineData("a/b c")]
    public async Task Put_RefusesAnInvalidKey(string key)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Target.PutAsync(key, new MemoryStream([1]), new BackupPutOptions(), Token));
    }
}
