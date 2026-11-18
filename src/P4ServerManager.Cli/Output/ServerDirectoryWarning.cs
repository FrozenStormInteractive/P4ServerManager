using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Output;

/// <summary>
/// Warns, before an instance is created, of a folder next to its server directory whose name differs by its case only.
/// The creation goes on: the warning only keeps the two folders from being mixed up.
/// </summary>
internal static class ServerDirectoryWarning
{
    public static async Task WriteAsync(
        CliInvocation invocation,
        DaemonClient client,
        string? name,
        string? serverDirectory,
        CancellationToken cancellationToken)
    {
        ServerDirectoryPreviewDto preview;
        try
        {
            preview = await client.PreviewServerDirectoryAsync(name, serverDirectory, cancellationToken);
        }
        catch (DaemonApiException)
        {
            // The creation reports what is wrong with the name or the folder.
            return;
        }

        if (preview.CaseVariant is { } caseVariant)
        {
            await invocation.Progress.WriteLineAsync(
                $"Warning: {caseVariant} already exists, and only the case of its name tells it from the server "
                + $"directory {preview.Path}. Both are kept apart, but they are easy to mix up, especially from Windows.");
        }
    }
}
