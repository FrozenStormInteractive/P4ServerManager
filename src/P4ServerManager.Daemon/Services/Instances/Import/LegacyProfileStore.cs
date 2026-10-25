namespace P4ServerManager.Daemon.Services.Instances.Import;

/// <summary>
/// The profiles of the HelixCoreServer Synology package waiting in <see cref="DaemonPaths.LegacyProfilesDirectory"/>.
/// The package copies them there at each start; the daemon never changes them, it only moves away the ones the
/// user dismisses.
/// </summary>
public sealed class LegacyProfileStore(DaemonPaths paths, ILogger<LegacyProfileStore> logger)
{
    /// <summary>
    /// File p4d creates in every root. Its presence tells a root that holds a database.
    /// </summary>
    private const string CountersTable = "db.counters";

    /// <summary>
    /// Folders p4dctl-ng creates next to the root of a server.
    /// </summary>
    private static readonly string[] ServerFolders = ["archives", "journals", "logs"];

    /// <returns>The profiles, by file name, with the problems of their root.</returns>
    public async Task<IReadOnlyList<LegacyProfile>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.LegacyProfilesDirectory))
        {
            return [];
        }

        List<LegacyProfile> profiles = [];
        foreach (string file in Directory.EnumerateFiles(paths.LegacyProfilesDirectory).Order(StringComparer.Ordinal))
        {
            profiles.Add(await ReadAsync(file, cancellationToken));
        }
        return profiles;
    }

    /// <returns>The profile, or null when no profile has this identifier.</returns>
    public async Task<LegacyProfile?> FindAsync(string id, CancellationToken cancellationToken)
    {
        string? file = GetFile(id);
        return file is null ? null : await ReadAsync(file, cancellationToken);
    }

    /// <summary>
    /// Moves a profile to <see cref="DaemonPaths.DismissedLegacyProfilesDirectory"/>, where it is no longer listed.
    /// </summary>
    /// <returns>False when no profile has this identifier.</returns>
    public bool Dismiss(string id)
    {
        string? file = GetFile(id);
        if (file is null)
        {
            return false;
        }

        Directory.CreateDirectory(paths.DismissedLegacyProfilesDirectory);
        string target = Path.Combine(paths.DismissedLegacyProfilesDirectory, id);
        File.Move(file, target, overwrite: true);
        logger.LogInformation("Dismissed the legacy profile {Id}", id);
        return true;
    }

    /// <summary>
    /// Checks that a root holds a p4d database the daemon can open, and that no server uses it.
    /// </summary>
    /// <returns>Why the root cannot be imported, or null when it can.</returns>
    public static string? CheckRoot(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return $"The root {rootPath} does not exist, or the daemon cannot see it.";
        }

        string counters = Path.Combine(rootPath, CountersTable);
        if (!File.Exists(counters))
        {
            return $"The root {rootPath} holds no p4d database.";
        }

        try
        {
            // Opened for writing, and never written: p4d needs to write to it, as the user the daemon runs as.
            using FileStream stream = new(counters, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            return $"The daemon cannot write to the root {rootPath}. Give the account it runs as access to it.";
        }
        catch (IOException exception)
        {
            return $"The database in {rootPath} cannot be opened: {exception.Message}";
        }

        // p4dctl-ng puts the archives, the journals and the logs next to the root, and points the server to them.
        string? serverDirectory = Path.GetDirectoryName(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar));
        IEnumerable<string> folders = serverDirectory is null
            ? [rootPath]
            : [rootPath, .. ServerFolders.Select(name => Path.Combine(serverDirectory, name)).Where(Directory.Exists)];
        foreach (string folder in folders)
        {
            if (!CanWriteTo(folder))
            {
                return $"The daemon cannot write to {folder}. Give the account it runs as access to it.";
            }
        }
        return null;
    }

    /// <summary>
    /// Creates and deletes a file in the folder: the only check that also follows the ACLs of DSM.
    /// </summary>
    private static bool CanWriteTo(string folder)
    {
        try
        {
            using FileStream stream = new(
                Path.Combine(folder, $".p4sm-write-check-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? GetFile(string id)
    {
        // A file name of the folder, never a path that leads out of it.
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id) || id is "." or "..")
        {
            return null;
        }

        string file = Path.Combine(paths.LegacyProfilesDirectory, id);
        return File.Exists(file) ? file : null;
    }

    private async Task<LegacyProfile> ReadAsync(string file, CancellationToken cancellationToken)
    {
        string id = Path.GetFileName(file);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the legacy profile {Id}", id);
            return new LegacyProfile(id, null, null, null, true, false, [], [$"The profile cannot be read: {exception.Message}"]);
        }

        LegacyProfile profile = LegacyProfileParser.Parse(id, content);
        if (profile.CanImport && CheckRoot(profile.RootPath!) is { } problem)
        {
            profile = profile with { Problems = [problem] };
        }
        return profile;
    }
}
