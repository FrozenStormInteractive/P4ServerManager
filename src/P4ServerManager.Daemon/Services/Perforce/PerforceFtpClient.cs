using System.Net;

using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Size announced by the server, when known.</param>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>
/// Reads the Perforce FTP, through the <see cref="HttpClientName"/> client whose base address is the root that lists
/// the <c>rYY.N/</c> folders.
/// </summary>
public sealed class PerforceFtpClient(IHttpClientFactory httpClientFactory, P4ServerPlatformDefinition platformDefinition)
{
    public const string HttpClientName = "PerforceFtp";

    private const int BufferSize = 81920;

    public async Task<IReadOnlyList<P4ServerRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        string html = await httpClient.GetStringAsync("", cancellationToken);
        return ReleaseIndexParser.Parse(html);
    }

    /// <summary>
    /// Reads the checksums of the release for the current platform.
    /// </summary>
    /// <returns>The hashes by file name, or null when the release has no build for this platform.</returns>
    public async Task<IReadOnlyDictionary<string, string>?> GetChecksumsAsync(
        P4ServerRelease release,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.GetAsync(
            GetFileUrl(release, "SHA256SUMS"),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        return Sha256SumsParser.Parse(content);
    }

    public async Task DownloadAsync(
        P4ServerRelease release,
        string fileName,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.GetAsync(
            GetFileUrl(release, fileName),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long bytesReceived = 0;
        int read;
        progress?.Report(new DownloadProgress(0, totalBytes));
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReceived += read;
            progress?.Report(new DownloadProgress(bytesReceived, totalBytes));
        }
    }

    private string GetFileUrl(P4ServerRelease release, string fileName)
    {
        return $"{release.Name}/{platformDefinition.FtpDirectory}/{fileName}";
    }
}
