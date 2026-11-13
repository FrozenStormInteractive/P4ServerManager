using System.Security.Cryptography;

using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Tests.Repository;

public sealed class BackupRepositoryTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalBackupTarget _target;
    private readonly BackupRepositoryOptions _options;

    public BackupRepositoryTests()
    {
        _target = new LocalBackupTarget(Path.Combine(_directory, "repository"));
        // Few iterations: the tests open many repositories.
        _options = new BackupRepositoryOptions(Path.Combine(_directory, "tmp")) { KeyDerivationIterations = 1000 };
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Open_WithThePassphrase_OpensTheRepositoryItWasCreatedAs()
    {
        Guid repositoryId;
        using (BackupRepository created = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token))
        {
            repositoryId = created.RepositoryId;
        }

        (await BackupRepository.ExistsAsync(_target, Token)).ShouldBeTrue();
        using BackupRepository opened = await BackupRepository.OpenAsync(_target, Passphrase, _options, Token);
        opened.RepositoryId.ShouldBe(repositoryId);
    }

    [Fact]
    public async Task Open_RefusesAWrongPassphrase()
    {
        (await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token)).Dispose();

        await Should.ThrowAsync<BackupRepositoryPassphraseException>(() =>
            BackupRepository.OpenAsync(_target, "wrong", _options, Token));
    }

    [Fact]
    public async Task Open_RefusesATargetWithoutRepository()
    {
        await Should.ThrowAsync<BackupRepositoryException>(() =>
            BackupRepository.OpenAsync(_target, Passphrase, _options, Token));
    }

    [Fact]
    public async Task Initialize_RefusesATargetThatAlreadyHoldsARepository()
    {
        (await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token)).Dispose();

        await Should.ThrowAsync<BackupRepositoryException>(() =>
            BackupRepository.InitializeAsync(_target, Passphrase, _options, Token));
    }

    [Fact]
    public async Task Config_HoldsNoKeyInClear()
    {
        (await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token)).Dispose();

        string config = await File.ReadAllTextAsync(Path.Combine(_directory, "repository", "config"), Token);
        config.ShouldContain("pbkdf2-sha256");
        config.ShouldNotContain(Passphrase);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, false)]
    [InlineData(ObjectFormat.FrameSize, true)]
    [InlineData((3 * ObjectFormat.FrameSize) + 17, false)]
    [InlineData((2 * ObjectFormat.FrameSize) + 5, true)]
    public async Task PutFile_ThenOpenObject_ReturnsTheContent(int size, bool compress)
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        byte[] content = CreateContent(size);
        string file = await WriteFileAsync(content);

        StoredObject stored = await repository.PutFileAsync(file, compress, Token);

        stored.Uploaded.ShouldBeTrue();
        stored.Size.ShouldBe(size);
        stored.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
        stored.Name.ShouldBe(repository.GetObjectName(stored.Sha256));
        (await ReadObjectAsync(repository, stored.Name)).ShouldBe(content);
        Directory.EnumerateFiles(Path.Combine(_directory, "tmp")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PutFile_CompressesACompressibleContent()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        string file = await WriteFileAsync(new byte[1024 * 1024]);

        StoredObject stored = await repository.PutFileAsync(file, compress: true, Token);

        stored.StoredSize.ShouldBeLessThan(10_000);
    }

    [Fact]
    public async Task PutFile_DoesNotUploadAContentTheRepositoryHas()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        byte[] content = CreateContent(5000);

        StoredObject first = await repository.PutFileAsync(await WriteFileAsync(content), compress: true, Token);
        StoredObject second = await repository.PutFileAsync(await WriteFileAsync(content), compress: true, Token);

        second.Uploaded.ShouldBeFalse();
        second.Name.ShouldBe(first.Name);
        (await repository.ObjectExistsAsync(first.Name, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task ObjectNames_DependOnTheRepository()
    {
        using BackupRepository one = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        using BackupRepository other = await BackupRepository.InitializeAsync(
            new LocalBackupTarget(Path.Combine(_directory, "other")),
            Passphrase,
            _options,
            Token);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));

        one.GetObjectName(sha256).ShouldNotBe(other.GetObjectName(sha256));
        one.GetObjectName(sha256).ShouldNotBe(sha256);
    }

    [Fact]
    public async Task OpenObject_DetectsAChangedByte()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        StoredObject stored = await repository.PutFileAsync(await WriteFileAsync(CreateContent(5000)), false, Token);
        string path = GetStoredPath(stored.Name);
        byte[] bytes = await File.ReadAllBytesAsync(path, Token);
        bytes[100] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, Token);

        await Should.ThrowAsync<BackupIntegrityException>(() => ReadObjectAsync(repository, stored.Name));
    }

    [Fact]
    public async Task OpenObject_DetectsAnObjectCutShortOnAFrameBoundary()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        StoredObject stored = await repository.PutFileAsync(
            await WriteFileAsync(CreateContent((2 * ObjectFormat.FrameSize) + 10)),
            false,
            Token);
        string path = GetStoredPath(stored.Name);
        byte[] bytes = await File.ReadAllBytesAsync(path, Token);
        int firstTwoFrames = ObjectFormat.HeaderSize
            + (2 * (ObjectFormat.FrameHeaderSize + ObjectFormat.FrameSize + ObjectFormat.TagSize));
        await File.WriteAllBytesAsync(path, bytes[..firstTwoFrames], Token);

        await Should.ThrowAsync<BackupIntegrityException>(() => ReadObjectAsync(repository, stored.Name));
    }

    [Fact]
    public async Task OpenObject_RefusesAnObjectOfAnotherRepository()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        LocalBackupTarget otherTarget = new(Path.Combine(_directory, "other"));
        using BackupRepository other = await BackupRepository.InitializeAsync(otherTarget, Passphrase, _options, Token);
        StoredObject stored = await other.PutFileAsync(await WriteFileAsync(CreateContent(100)), false, Token);
        string key = BackupRepository.GetObjectKey(stored.Name);
        await using (Stream source = await otherTarget.OpenReadAsync(key, Token))
        {
            using MemoryStream copy = new();
            await source.CopyToAsync(copy, Token);
            copy.Position = 0;
            await _target.PutAsync(key, copy, new BackupPutOptions(), Token);
        }

        await Should.ThrowAsync<BackupIntegrityException>(() => ReadObjectAsync(repository, stored.Name));
    }

    [Fact]
    public async Task Manifest_IsReadBackAsItWasWritten_AndListedWithItsSource()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        Guid sourceId = Guid.NewGuid();
        BackupManifestHeader header = CreateHeader(sourceId, new DateTimeOffset(2026, 11, 10, 8, 30, 15, 123, TimeSpan.Zero));
        header.Labels["checkpoint"] = "12";
        BackupManifestEntry[] entries =
        [
            new("checkpoint", "journals/main.ckp.12.gz", 10, DateTimeOffset.UnixEpoch, new string('a', 64), "obj1"),
            new("archive", "depot/file.txt,v", 20, DateTimeOffset.UnixEpoch, new string('b', 64), "obj2"),
        ];

        BackupSnapshotInfo written = await repository.WriteManifestAsync(header, entries, Token);
        await repository.WriteManifestAsync(CreateHeader(Guid.NewGuid(), DateTimeOffset.UtcNow), [], Token);

        BackupManifest manifest = await repository.ReadManifestAsync(written.Key, Token);
        manifest.Header.Id.ShouldBe(header.Id);
        manifest.Header.CreatedAt.ShouldBe(header.CreatedAt);
        manifest.Header.Labels["checkpoint"].ShouldBe("12");
        manifest.Entries.ShouldBe(entries);

        List<BackupSnapshotInfo> snapshots = [];
        await foreach (BackupSnapshotInfo snapshot in repository.ListSnapshotsAsync(sourceId, Token))
        {
            snapshots.Add(snapshot);
        }
        snapshots.ShouldBe([written]);
    }

    [Fact]
    public async Task WriteManifest_RefusesToReplaceAPoint()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        BackupManifestHeader header = CreateHeader(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await repository.WriteManifestAsync(header, [], Token);

        await Should.ThrowAsync<BackupObjectExistsException>(() => repository.WriteManifestAsync(header, [], Token));
    }

    [Fact]
    public async Task Verify_FindsNothingWrongWithACompletePoint()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        BackupManifest manifest = await StoreManifestAsync(repository, 3);

        (await repository.VerifyAsync(manifest, deep: false, null, Token)).Succeeded.ShouldBeTrue();
        BackupVerificationResult deep = await repository.VerifyAsync(manifest, deep: true, null, Token);
        deep.Succeeded.ShouldBeTrue();
        deep.CheckedObjects.ShouldBe(3);
    }

    [Fact]
    public async Task Verify_ReportsAMissingObject_AndDeepVerifyAChangedOne()
    {
        using BackupRepository repository = await BackupRepository.InitializeAsync(_target, Passphrase, _options, Token);
        BackupManifest manifest = await StoreManifestAsync(repository, 3);
        File.Delete(GetStoredPath(manifest.Entries[0].Object));
        string changed = GetStoredPath(manifest.Entries[1].Object);
        byte[] bytes = await File.ReadAllBytesAsync(changed, Token);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(changed, bytes, Token);

        BackupVerificationResult quick = await repository.VerifyAsync(manifest, deep: false, null, Token);
        BackupVerificationResult deep = await repository.VerifyAsync(manifest, deep: true, null, Token);

        quick.Problems.Select(p => p.Path).ShouldBe([manifest.Entries[0].Path]);
        deep.Problems.Select(p => p.Path).OrderBy(p => p).ShouldBe(
            [manifest.Entries[0].Path, manifest.Entries[1].Path]);
    }

    private async Task<BackupManifest> StoreManifestAsync(BackupRepository repository, int files)
    {
        List<BackupManifestEntry> entries = [];
        for (int i = 0; i < files; i++)
        {
            string file = await WriteFileAsync(CreateContent(1000 + i));
            StoredObject stored = await repository.PutFileAsync(file, compress: true, Token);
            entries.Add(new BackupManifestEntry(
                "archive",
                $"depot/file{i}",
                stored.Size,
                DateTimeOffset.UnixEpoch,
                stored.Sha256,
                stored.Name));
        }
        BackupManifestHeader header = CreateHeader(Guid.NewGuid(), DateTimeOffset.UtcNow);
        BackupSnapshotInfo snapshot = await repository.WriteManifestAsync(header, entries, Token);
        return await repository.ReadManifestAsync(snapshot.Key, Token);
    }

    private static BackupManifestHeader CreateHeader(Guid sourceId, DateTimeOffset createdAt)
    {
        return new BackupManifestHeader
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            SourceName = "main",
            Kind = "full",
            CreatedAt = createdAt,
        };
    }

    private static byte[] CreateContent(int size)
    {
        return RandomNumberGenerator.GetBytes(size);
    }

    private async Task<string> WriteFileAsync(byte[] content)
    {
        string path = Path.Combine(_directory, "files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, Token);
        return path;
    }

    private string GetStoredPath(string name)
    {
        return Path.Combine(_directory, "repository", "objects", name[..2], name[2..4], name);
    }

    private static async Task<byte[]> ReadObjectAsync(BackupRepository repository, string name)
    {
        await using Stream stream = await repository.OpenObjectAsync(name, Token);
        using MemoryStream memory = new();
        await stream.CopyToAsync(memory, Token);
        return memory.ToArray();
    }
}
