using System.Formats.Tar;
using System.IO.Compression;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Tests.Services.Perforce;

using static P4ServerManager.Daemon.Tests.Services.Perforce.BuildTestEnvironment;

namespace P4ServerManager.Daemon.Tests.Services.Builds;

public sealed class BuildImporterTests : IAsyncLifetime
{
    private BuildTestEnvironment _environment = null!;
    private BuildImporter _importer = null!;

    public async ValueTask InitializeAsync()
    {
        _environment = await BuildTestEnvironment.CreateAsync();
        _importer = new BuildImporter(_environment.Cache, PlatformDefinition);
    }

    public async ValueTask DisposeAsync()
    {
        await _environment.DisposeAsync();
    }

    [Fact]
    public async Task ImportFilesAsync_ArchivesP4dAndP4()
    {
        using MemoryStream p4d = new(P4dContent("2024.2/2700000"));
        using MemoryStream p4 = new(P4Content("2024.2/2700000"));

        P4ServerBuild build = await _importer.ImportFilesAsync(p4d, p4, TestContext.Current.CancellationToken);

        build.BuildNumber.ShouldBe("2700000");
        build.Imported.ShouldBeTrue();
        _environment.Archive.Exists(build).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_environment.DataDirectory, "builds", ".tmp")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportPackageAsync_TakesP4dAndP4FromTheArchive(bool gzip)
    {
        using MemoryStream package = CreatePackage(
            gzip,
            ("helix-core-server/p4d", P4dContent("2025.1/2850123")),
            ("helix-core-server/p4", P4Content("2025.1/2850123")),
            ("helix-core-server/p4broker", "broker"u8.ToArray()));

        P4ServerBuild build = await _importer.ImportPackageAsync(package, TestContext.Current.CancellationToken);

        build.Version.ShouldBe("2025.1");
        build.BuildNumber.ShouldBe("2850123");
        build.P4Sha256.ShouldBe(Hash(P4Content("2025.1/2850123")));
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesAPackageWithoutP4()
    {
        using MemoryStream package = CreatePackage(true, ("p4d", P4dContent("2025.1/2850123")));

        P4ServerBuildException exception = await Should.ThrowAsync<P4ServerBuildException>(
            () => _importer.ImportPackageAsync(package, TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("The package has no p4.");
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesAFileThatIsNotAnArchive()
    {
        using MemoryStream package = new("not an archive at all, but long enough to be read as a tar header"u8.ToArray());

        await Should.ThrowAsync<P4ServerBuildException>(
            () => _importer.ImportPackageAsync(package, TestContext.Current.CancellationToken));
    }

    private static MemoryStream CreatePackage(bool gzip, params (string Name, byte[] Content)[] files)
    {
        MemoryStream package = new();
        GZipStream? compression = gzip ? new GZipStream(package, CompressionLevel.Fastest, leaveOpen: true) : null;
        using (TarWriter tar = new(compression ?? (Stream)package, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }
        compression?.Dispose();
        package.Position = 0;
        return package;
    }
}
