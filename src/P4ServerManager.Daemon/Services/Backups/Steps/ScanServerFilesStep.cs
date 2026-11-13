using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Steps;

/// <summary>
/// Lists the archives of every depot and the files of the root the checkpoint does not hold, once the database is
/// frozen: an archive written meanwhile is harmless, it only goes unused. Only the files whose size or date changed
/// since the last backup are read, to hash them.
/// </summary>
public sealed class ScanServerFilesStep : IOperationStep<BackupContext>
{
    private const int ReportEvery = 500;

    public string Key => "scan-server-files";

    public string Title => "List the files of the server";

    public async Task<string?> RunAsync(
        BackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        ServerFileLayout layout = context.Layout
            ?? throw new InvalidOperationException("The layout of the server was not read.");
        BackupFileIndex index = context.Index
            ?? throw new InvalidOperationException("The index of the files was not loaded.");

        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in ListRootFiles(layout.RootPath))
        {
            string relative = Path.GetRelativePath(layout.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            await AddAsync(BackupFileRoles.RootFile, $"root/{relative}", path);
        }

        foreach (DepotLocation depot in layout.Depots)
        {
            if (!Directory.Exists(depot.Directory))
            {
                // A depot nothing was submitted to yet has no folder.
                continue;
            }
            if (Contains(depot.Directory, layout.RootPath))
            {
                throw new InstanceOperationException(
                    $"Depot {depot.Name} keeps its files in {depot.Directory}, which holds the root of the server: such a layout cannot be backed up.");
            }

            EnumerationOptions enumeration = new()
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            };
            foreach (string path in Directory.EnumerateFiles(depot.Directory, "*", enumeration))
            {
                string relative = Path.GetRelativePath(depot.Directory, path).Replace(Path.DirectorySeparatorChar, '/');
                await AddAsync(BackupFileRoles.Archive, $"depots/{depot.Name}/{relative}", path);
            }
        }

        List<BackupFile> files = [.. context.MetadataFiles, .. context.ServerFiles];
        context.Run.FileCount = files.Count;
        context.Run.TotalBytes = files.Sum(f => f.Size);
        return $"{context.ServerFiles.Count} files, {index.HashedFiles} read";

        async Task AddAsync(string role, string manifestPath, string fullPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Two depots can share a folder: a file is backed up once.
            if (!seen.Add(fullPath))
            {
                return;
            }

            BackupFile? file = await index.ReadAsync(role, manifestPath, fullPath, cancellationToken);
            if (file is not null)
            {
                context.ServerFiles.Add(file);
                if (context.ServerFiles.Count % ReportEvery == 0)
                {
                    progress.Report(new OperationStepProgress(
                        $"{context.ServerFiles.Count} files, {index.HashedFiles} read",
                        null));
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="directory"/> or inside it.
    /// </summary>
    private static bool Contains(string directory, string path)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return child.Equals(parent, comparison)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The files of the root a checkpoint does not hold: the server ID, the license, and the SSL key and certificate.
    /// </summary>
    private static IEnumerable<string> ListRootFiles(string rootPath)
    {
        foreach (string name in new[] { ServerLayout.ServerIdFile, "license" })
        {
            string path = Path.Combine(rootPath, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        string sslDirectory = ServerLayout.GetSslDirectory(rootPath);
        if (Directory.Exists(sslDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(sslDirectory))
            {
                yield return path;
            }
        }
    }
}
