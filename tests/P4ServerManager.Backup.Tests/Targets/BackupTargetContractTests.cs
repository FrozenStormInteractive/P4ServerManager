using System.Security.Cryptography;

using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Tests.Targets;

/// <summary>
/// What every storage target must do, run against each of them. A test of a capability runs only on a target that
/// declares it, and is reported as skipped otherwise: it is never green by default.
/// </summary>
public abstract class BackupTargetContractTests : IAsyncLifetime
{
    /// <summary>
    /// Larger than a part of a multipart upload of the cloud SDKs.
    /// </summary>
    private const int LargeObjectSize = 20 * 1024 * 1024;

    private IBackupTarget? _target;

    protected IBackupTarget Target => _target ?? throw new InvalidOperationException("The target is not created.");

    /// <summary>
    /// Creates a target on an empty place: a new folder, or a new random prefix of a bucket. Returns null to skip
    /// the tests, such as when Docker or the credentials of a provider are missing.
    /// </summary>
    protected abstract Task<IBackupTarget?> CreateTargetAsync();

    /// <summary>
    /// Capabilities the target declares but its emulator lacks: their tests are skipped for this run.
    /// </summary>
    protected virtual TargetCapabilities UnsupportedByEnvironment => TargetCapabilities.None;

    /// <summary>
    /// Objects of the listing test: above one page of the cloud APIs. A target that lists everything in one call, such
    /// as FTP, can lower it to keep the test short.
    /// </summary>
    protected virtual int ManyObjectsCount => 1005;

    public async ValueTask InitializeAsync()
    {
        _target = await CreateTargetAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_target is not null)
        {
            await foreach (BackupObjectInfo info in _target.ListAsync(string.Empty, CancellationToken.None))
            {
                await _target.DeleteAsync(info.Key, CancellationToken.None);
            }
            await _target.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Put_ThenOpenRead_ReturnsTheContent()
    {
        SkipWithoutTarget();
        byte[] content = RandomNumberGenerator.GetBytes(1000);

        await Target.PutAsync("a/b/object", new MemoryStream(content), new BackupPutOptions(), Token);

        (await ReadAllAsync("a/b/object")).ShouldBe(content);
        (await Target.ExistsAsync("a/b/object", Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task Put_StreamsALargeObject()
    {
        SkipWithoutTarget();
        string path = Path.GetTempFileName();
        try
        {
            byte[] content = RandomNumberGenerator.GetBytes(LargeObjectSize);
            await File.WriteAllBytesAsync(path, content, Token);
            await using (FileStream file = File.OpenRead(path))
            {
                await Target.PutAsync("large", file, new BackupPutOptions(), Token);
            }

            (await ReadAllAsync("large")).ShouldBe(content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Put_ReplacesAnExistingObject_WithoutCondition()
    {
        SkipWithoutTarget();
        await Target.PutAsync("object", new MemoryStream([1]), new BackupPutOptions(), Token);

        await Target.PutAsync("object", new MemoryStream([2, 3]), new BackupPutOptions(), Token);

        (await ReadAllAsync("object")).ShouldBe([2, 3]);
    }

    [Fact]
    public async Task Put_IfNotExists_RefusesAnExistingKey()
    {
        SkipWithout(TargetCapabilities.ConditionalWrite);
        await Target.PutAsync("object", new MemoryStream([1]), new BackupPutOptions(IfNotExists: true), Token);

        await Should.ThrowAsync<BackupObjectExistsException>(() =>
            Target.PutAsync("object", new MemoryStream([2]), new BackupPutOptions(IfNotExists: true), Token));

        (await ReadAllAsync("object")).ShouldBe([1]);
    }

    [Fact]
    public async Task Put_WithServerSideChecksum_RefusesAContentThatDoesNotMatch()
    {
        SkipWithout(TargetCapabilities.ServerSideChecksum);
        byte[] wrongSha256 = SHA256.HashData([9, 9, 9]);

        await Should.ThrowAsync<Exception>(() =>
            Target.PutAsync("object", new MemoryStream([1, 2, 3]), new BackupPutOptions(Sha256: wrongSha256), Token));

        (await Target.ExistsAsync("object", Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenRead_ThrowsForAMissingObject()
    {
        SkipWithoutTarget();

        await Should.ThrowAsync<BackupObjectNotFoundException>(() => Target.OpenReadAsync("missing", Token));
        (await Target.ExistsAsync("missing", Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_ReturnsTheObjectsUnderThePrefixOnly()
    {
        SkipWithoutTarget();
        await Target.PutAsync("objects/aa/one", new MemoryStream([1]), new BackupPutOptions(), Token);
        await Target.PutAsync("objects/ab/two", new MemoryStream([1, 2]), new BackupPutOptions(), Token);
        await Target.PutAsync("snapshots/three", new MemoryStream([1]), new BackupPutOptions(), Token);

        List<BackupObjectInfo> objects = await ListAsync("objects/");

        objects.OrderBy(o => o.Key).ShouldBe(
        [
            new BackupObjectInfo("objects/aa/one", 1),
            new BackupObjectInfo("objects/ab/two", 2),
        ]);
        (await ListAsync("objects/aa")).Select(o => o.Key).ShouldBe(["objects/aa/one"]);
        (await ListAsync("nothing/")).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_ReturnsMoreObjectsThanOnePage()
    {
        SkipWithoutTarget();
        int count = ManyObjectsCount;
        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, token) =>
            await Target.PutAsync($"many/{i:D5}", new MemoryStream([1]), new BackupPutOptions(), token));

        (await ListAsync("many/")).Count.ShouldBe(count);
    }

    [Fact]
    public async Task Delete_RemovesTheObject_AndIgnoresAMissingOne()
    {
        SkipWithoutTarget();
        await Target.PutAsync("object", new MemoryStream([1]), new BackupPutOptions(), Token);

        await Target.DeleteAsync("object", Token);
        await Target.DeleteAsync("object", Token);

        (await Target.ExistsAsync("object", Token)).ShouldBeFalse();
    }

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected void SkipWithoutTarget()
    {
        Assert.SkipWhen(_target is null, "The target is not available in this environment.");
    }

    protected void SkipWithout(TargetCapabilities capability)
    {
        SkipWithoutTarget();
        Assert.SkipUnless(Target.Capabilities.HasFlag(capability), $"The target does not declare {capability}.");
        Assert.SkipWhen(
            UnsupportedByEnvironment.HasFlag(capability),
            $"The emulator of the target does not support {capability}.");
    }

    private async Task<byte[]> ReadAllAsync(string key)
    {
        await using Stream stream = await Target.OpenReadAsync(key, Token);
        using MemoryStream memory = new();
        await stream.CopyToAsync(memory, Token);
        return memory.ToArray();
    }

    private async Task<List<BackupObjectInfo>> ListAsync(string prefix)
    {
        List<BackupObjectInfo> objects = [];
        await foreach (BackupObjectInfo info in Target.ListAsync(prefix, Token))
        {
            objects.Add(info);
        }
        return objects;
    }
}
