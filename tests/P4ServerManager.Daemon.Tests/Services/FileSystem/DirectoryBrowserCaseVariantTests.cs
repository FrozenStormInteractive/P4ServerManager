using P4ServerManager.Daemon.Services.FileSystem;

namespace P4ServerManager.Daemon.Tests.Services.FileSystem;

public sealed class DirectoryBrowserCaseVariantTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));

    public DirectoryBrowserCaseVariantTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FindCaseVariant_ReturnsNothingWithoutAnEntryOfTheSameName()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Other"));

        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "Main")).ShouldBeNull();
    }

    [Fact]
    public void FindCaseVariant_ReturnsNothingForTheEntryItself()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Main"));

        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "Main")).ShouldBeNull();
    }

    [Fact]
    public void FindCaseVariant_ReturnsNothingWhenTheParentIsMissing()
    {
        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "missing", "Main")).ShouldBeNull();
    }

    [Fact]
    public void FindCaseVariant_FindsAnEntryWhoseNameDiffersByItsCaseOnly()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            "The file system ignores the case: both names are the same entry.");
        string variant = Path.Combine(_directory, "main");
        Directory.CreateDirectory(variant);

        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "Main")).ShouldBe(variant);
    }

    [Fact]
    public void FindCaseVariant_FindsTheVariantNextToTheEntryItself()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            "The file system ignores the case: both names are the same entry.");
        Directory.CreateDirectory(Path.Combine(_directory, "Main"));
        string variant = Path.Combine(_directory, "MAIN");
        File.WriteAllText(variant, "a file");

        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "Main")).ShouldBe(variant);
    }

    [Fact]
    public void FindCaseVariant_TakesWildcardsAsCharacters()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Main"));

        DirectoryBrowser.FindCaseVariant(Path.Combine(_directory, "M*")).ShouldBeNull();
    }
}
