using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Tests.Services.Perforce;

public class ParsingTests
{
    [Fact]
    public void ReleaseIndexParser_ReturnsDistinctReleasesNewestFirst()
    {
        const string html = """
            <a href="../">../</a>
            <a href="r19.2/">r19.2/</a>
            <a href="r24.2/">r24.2/</a>
            <a href="r25.1/">r25.1/</a>
            <a href="r24.10/">r24.10/</a>
            <a href="r25.1/">r25.1/</a>
            <a href="tools/">tools/</a>
            """;

        IReadOnlyList<P4ServerRelease> releases = ReleaseIndexParser.Parse(html);

        releases.Select(r => r.Name).ShouldBe(["r25.1", "r24.2", "r19.2"]);
    }

    [Fact]
    public void Sha256SumsParser_ReadsBinaryAndTextModeLines()
    {
        const string content = """
            ad1d4f10d22f74dbe85ec01bc5ab7870d40e829b054a7d1c5f6fd9cf71e4904b *p4d
            9F93BF94DB927EF36A74B6A3AB79A9598C8FE1AA8BF5B9748E3B7792F103B180  p4

            not a checksum line
            """;

        IReadOnlyDictionary<string, string> hashes = Sha256SumsParser.Parse(content);

        hashes.Count.ShouldBe(2);
        hashes["p4d"].ShouldBe("ad1d4f10d22f74dbe85ec01bc5ab7870d40e829b054a7d1c5f6fd9cf71e4904b");
        hashes["p4"].ShouldBe("9f93bf94db927ef36a74b6a3ab79a9598c8fe1aa8bf5b9748e3b7792f103b180");
    }

    [Theory]
    [InlineData("Rev. P4D/LINUX26X86_64/2025.1/2850123 (2025/09/28).", "2025.1", "2850123")]
    [InlineData("Copyright 1995-2025 Perforce Software.\nRev. P4D/NTX64/2024.2/2726395 (2025/01/13).", "2024.2", "2726395")]
    [InlineData("Rev. P4D/LINUX26AARCH64/2023.1.PATCH/2468153 (2023/07/24).", "2023.1", "2468153")]
    public void P4dVersionParser_ReadsVersionAndBuild(string output, string version, string build)
    {
        P4ServerVersionParser.TryParse(output, out P4ServerVersionInfo? result).ShouldBeTrue();

        result.ShouldBe(new P4ServerVersionInfo(version, build));
    }

    [Fact]
    public void P4dVersionParser_RejectsOtherOutput()
    {
        P4ServerVersionParser.TryParse("Perforce client error", out P4ServerVersionInfo? result).ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact]
    public void SslFingerprintParser_ReadsTheOutputOfP4dGf()
    {
        SslFingerprintParser.Parse("Fingerprint: a5:C8:59:F1:AC:B9:85:90:D9:6D:4A:3F:70:2A:F9:2C:F6:CB:12:F8\r\n")
            .ShouldBe("A5:C8:59:F1:AC:B9:85:90:D9:6D:4A:3F:70:2A:F9:2C:F6:CB:12:F8");
        SslFingerprintParser.Parse("Unable to get certificate.").ShouldBeNull();
    }

    [Fact]
    public void P4dRelease_ComparesByYearThenNumber()
    {
        P4ServerRelease.TryParse("r24.2", out P4ServerRelease older).ShouldBeTrue();
        P4ServerRelease.TryParse("r25.1", out P4ServerRelease newer).ShouldBeTrue();

        (older < newer).ShouldBeTrue();
        newer.Version.ShouldBe("2025.1");
        P4ServerRelease.TryParse("25.1", out _).ShouldBeFalse();
    }
}
