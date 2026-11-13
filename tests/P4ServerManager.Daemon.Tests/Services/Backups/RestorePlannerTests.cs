using P4ServerManager.Backup.Manifests;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

public sealed class RestorePlannerTests
{
    private static readonly string Server = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "restored"));

    private static readonly string Root = Path.Combine(Server, "root");

    private static readonly string Elsewhere = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere"));

    private static readonly RestorePlanOptions KeepLayout = new(
        RelocateDepots: false,
        RestoreServerId: true,
        RestoreLicense: true,
        ForbiddenDirectories: []);

    private static readonly RestorePlanOptions NewInstance = new(
        RelocateDepots: true,
        RestoreServerId: false,
        RestoreLicense: true,
        ForbiddenDirectories: []);

    private static readonly Dictionary<string, string> StandardConfigurables = new()
    {
        ["journalPrefix"] = "../journals/main",
        ["server.depot.root"] = "../archives",
    };

    [Fact]
    public void Plan_PlacesEachKindOfFile_InTheLayoutOfTheServer()
    {
        DepotRecord[] depots = [new("depot", 0, "depot/..."), new("spec", 2, "spec/...")];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        RestorePlan plan = RestorePlanner.Plan(
            [
                Entry("checkpoint", "journals/main.ckp.12.gz"),
                Entry("checksum", "journals/main.ckp.12.md5"),
                Entry("journal", "journals/main.jnl.12.gz"),
                Entry("root-file", "root/server.id"),
                Entry("root-file", "root/license"),
                Entry("root-file", "root/ssl/certificate.txt"),
                Entry("archive", "archives/depot/a/file.txt,v"),
                Entry("archive", "archives/spec/user/bob.p4s,v"),
            ],
            layout,
            layout,
            depots,
            KeepLayout);

        plan.Metadata.Select(f => f.Path).ShouldBe(
        [
            Path.Combine(Server, "journals", "main.ckp.12.gz"),
            Path.Combine(Server, "journals", "main.ckp.12.md5"),
            Path.Combine(Server, "journals", "main.jnl.12.gz"),
        ]);
        plan.Files.Select(f => f.Path).ShouldBe(
        [
            Path.Combine(Root, "server.id"),
            Path.Combine(Root, "license"),
            Path.Combine(Root, "ssl", "certificate.txt"),
            Path.Combine(Server, "archives", "depot", "a", "file.txt,v"),
            Path.Combine(Server, "archives", "spec", "user", "bob.p4s,v"),
        ]);
        plan.Relocations.ShouldBeEmpty();
        plan.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_LeavesOutTheServerIdAndTheLicense_WhenAsked()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, []);

        RestorePlan plan = RestorePlanner.Plan(
            [Entry("root-file", "root/server.id"), Entry("root-file", "root/license"), Entry("root-file", "root/ssl/privatekey.txt")],
            layout,
            layout,
            [],
            NewInstance with { RestoreLicense = false });

        plan.Files.Select(f => f.Path).ShouldBe([Path.Combine(Root, "ssl", "privatekey.txt")]);
    }

    [Fact]
    public void Plan_PutsTheArchivesOfAServerWithoutDepotRoot_BackInItsRoot()
    {
        DepotRecord[] depots = [new("depot", 0, "depot/...")];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, new Dictionary<string, string>(), depots);

        RestorePlan plan = RestorePlanner.Plan(
            [Entry("archive", "archives/depot/file.txt,v"), Entry("checkpoint", "journals/checkpoint.3")],
            layout,
            layout,
            depots,
            KeepLayout);

        plan.Files.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(Root, "depot", "file.txt,v"));
        plan.Metadata.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(Root, "checkpoint.3"));
    }

    [Fact]
    public void Plan_KeepsADepotOutsideTheDepotRoot_InItsFolder_WhenTheLayoutIsKept()
    {
        string assets = Path.Combine(Elsewhere, "assets");
        DepotRecord[] depots = [new("depot", 0, "depot/..."), new("assets", 0, assets + "/...")];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        RestorePlan plan = RestorePlanner.Plan(
            [Entry("archive", "depots/assets/big.bin,d/1.1.gz")],
            layout,
            layout,
            depots,
            KeepLayout);

        plan.Files.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(assets, "big.bin,d", "1.1.gz"));
        plan.Relocations.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_RelocatesADepotOutsideTheDepotRoot_AndTheDepotsInsideItsFolder_ForANewInstance()
    {
        string assets = Path.Combine(Elsewhere, "assets");
        DepotRecord[] depots =
        [
            new("depot", 0, "depot/..."),
            new("assets", 0, assets + "/..."),
            new("textures", 0, Path.Combine(assets, "textures") + "/..."),
        ];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        RestorePlan plan = RestorePlanner.Plan(
            [Entry("archive", "depots/assets/textures/wall.png,d/1.1.gz"), Entry("archive", "archives/depot/a.txt,v")],
            layout,
            layout,
            depots,
            NewInstance);

        plan.Files.Select(f => f.Path).ShouldBe(
        [
            Path.Combine(Server, "archives", "assets", "textures", "wall.png,d", "1.1.gz"),
            Path.Combine(Server, "archives", "depot", "a.txt,v"),
        ]);
        plan.Relocations.ShouldBe(
        [
            new DepotRelocation("assets", "assets/..."),
            new DepotRelocation("textures", "assets/textures/..."),
        ]);
    }

    [Fact]
    public void Plan_MovesTheArchivesOfAnImportedServer_ToTheLayoutOfTheNewInstance()
    {
        // Backed up from a server with an absolute server.depot.root and journals in its root; restored into the
        // standard layout of a new instance.
        string oldDepots = Path.Combine(Elsewhere, "depots");
        DepotRecord[] depots = [new("depot", 0, "depot/..."), new("pinned", 0, Path.Combine(oldDepots, "pinned") + "/...")];
        ServerFileLayout original = ServerFileLocator.Resolve(
            Root,
            new Dictionary<string, string> { ["server.depot.root"] = oldDepots },
            depots);
        ServerFileLayout target = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        RestorePlan plan = RestorePlanner.Plan(
            [
                Entry("checkpoint", "journals/checkpoint.8.gz"),
                Entry("archive", "archives/depot/a.txt,v"),
                Entry("archive", "archives/pinned/b.txt,v"),
            ],
            original,
            target,
            depots,
            NewInstance);

        plan.Metadata.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(Server, "journals", "checkpoint.8.gz"));
        plan.Files.Select(f => f.Path).ShouldBe(
        [
            Path.Combine(Server, "archives", "depot", "a.txt,v"),
            Path.Combine(Server, "archives", "pinned", "b.txt,v"),
        ]);
        plan.Relocations.ShouldBe([new DepotRelocation("pinned", "pinned/...")]);
    }

    [Fact]
    public void Plan_WarnsAboutTheDepotsOfAnObjectStore()
    {
        DepotRecord[] depots = [new("cloud", 0, "cloud/...", "s3,bucket:archives")];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        RestorePlan plan = RestorePlanner.Plan([], layout, layout, depots, NewInstance);

        plan.Warnings.ShouldHaveSingleItem().ShouldContain("cloud");
        plan.Relocations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("archives/../../etc/passwd")]
    [InlineData("archives/depot/./file")]
    [InlineData("archives//file")]
    [InlineData("root/../server.id")]
    [InlineData("journals/../main.ckp.1.gz")]
    public void Plan_RefusesAPathThatLeavesItsFolder(string path)
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, []);
        string role = path.StartsWith("journals/", StringComparison.Ordinal) ? "checkpoint"
            : path.StartsWith("root/", StringComparison.Ordinal) ? "root-file"
            : "archive";

        Should.Throw<InvalidDataException>(() => RestorePlanner.Plan([Entry(role, path)], layout, layout, [], KeepLayout));
    }

    [Fact]
    public void Plan_RefusesAFileOfADepotTheRestoredServerDoesNotHave()
    {
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, []);

        Should.Throw<InvalidDataException>(
            () => RestorePlanner.Plan([Entry("archive", "depots/gone/file,v")], layout, layout, [], KeepLayout))
            .Message.ShouldContain("depots/gone/file,v");
    }

    [Fact]
    public void Plan_RefusesToWriteInTheFoldersOfAnotherInstance()
    {
        string assets = Path.Combine(Elsewhere, "assets");
        DepotRecord[] depots = [new("assets", 0, assets + "/...")];
        ServerFileLayout layout = ServerFileLocator.Resolve(Root, StandardConfigurables, depots);

        Should.Throw<InvalidDataException>(() => RestorePlanner.Plan(
            [Entry("archive", "depots/assets/file,v")],
            layout,
            layout,
            depots,
            KeepLayout with { ForbiddenDirectories = [Elsewhere] }));
    }

    private static BackupManifestEntry Entry(string role, string path)
    {
        return new BackupManifestEntry(role, path, 1, DateTimeOffset.UnixEpoch, new string('a', 64), "object");
    }
}
