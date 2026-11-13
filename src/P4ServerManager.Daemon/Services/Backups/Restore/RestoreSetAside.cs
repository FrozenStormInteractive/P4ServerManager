using System.Text.Json;
using System.Text.Json.Serialization;

using P4ServerManager.Daemon.Services.Backups.Collection;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// The folders of an instance that a restore moved aside, so that it can put them back if it fails, even from a later
/// daemon process. Each folder is renamed next to itself, which is instant on any volume; each move is written to a
/// state file before it is made.
/// </summary>
public sealed class RestoreSetAside
{
    private const string AsideMarker = ".pre-restore-";

    private readonly string statePath;
    private readonly RestoreSetAsideState state;
    private readonly SemaphoreSlim reuseLock = new(1, 1);
    
    private string ReusedListPath => statePath + ".reused";

    /// <summary>
    /// Folders moved aside, or to be emptied on failure when they did not exist.
    /// </summary>
    public IReadOnlyList<SetAsideFolder> Folders => state.Folders;

    /// <summary>
    /// Whether p4d ran before the restore: it is started again after a rollback.
    /// </summary>
    public bool WasRunning => state.WasRunning;
    
    private RestoreSetAside(string statePath, RestoreSetAsideState state)
    {
        this.statePath = statePath;
        this.state = state;
    }

    /// <summary>
    /// Begins to keep the folders of a restore aside, in a new state file.
    /// </summary>
    /// <param name="statePath">The state file, in the folder of the instance.</param>
    /// <param name="runId">The restore.</param>
    /// <param name="wasRunning">Whether p4d ran before the restore.</param>
    /// <param name="cancellationToken">Aborts the save of the state.</param>
    public static async Task<RestoreSetAside> BeginAsync(
        string statePath,
        Guid runId,
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        RestoreSetAside setAside = new(statePath, new RestoreSetAsideState(runId, wasRunning, []));
        await setAside.SaveAsync(cancellationToken);
        return setAside;
    }

    /// <summary>
    /// Reads the state a restore left, if any.
    /// </summary>
    /// <exception cref="InvalidDataException">The state file cannot be read.</exception>
    public static async Task<RestoreSetAside?> LoadAsync(string statePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(statePath);
            RestoreSetAsideState state = await JsonSerializer.DeserializeAsync(
                stream,
                RestoreSetAsideJsonContext.Default.RestoreSetAsideState,
                cancellationToken)
                ?? throw new InvalidDataException($"{statePath} is empty.");
            return new RestoreSetAside(statePath, state);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{statePath} cannot be read: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is in a folder already kept aside.
    /// </summary>
    public bool Covers(string path)
    {
        return state.Folders.Any(f => ServerFileLocator.Contains(f.Original, path));
    }

    /// <summary>
    /// Moves a folder aside, unless one that holds it already is. A folder that does not exist is noted, so that a
    /// rollback removes what the restore put there.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Aborts the save of the state.</param>
    public async Task MoveAsideAsync(string path, CancellationToken cancellationToken)
    {
        string original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Covers(original))
        {
            return;
        }
        if (state.Folders.Any(f => ServerFileLocator.Contains(original, f.Original)))
        {
            throw new InvalidOperationException($"{original} holds a folder already kept aside.");
        }

        bool exists = Directory.Exists(original);
        string? aside = exists ? $"{original}{AsideMarker}{state.RunId.ToString("N")[..8]}" : null;
        if (aside is not null && (Directory.Exists(aside) || File.Exists(aside)))
        {
            throw new IOException($"{aside} already exists: remove it before the restore.");
        }

        state.Folders.Add(new SetAsideFolder(original, aside));
        await SaveAsync(cancellationToken);
        if (aside is not null)
        {
            Directory.Move(original, aside);
        }
    }

    /// <summary>
    /// Where the previous version of <paramref name="path"/> is kept, if it was in a folder moved aside.
    /// </summary>
    public string? FindPrevious(string path)
    {
        foreach (SetAsideFolder folder in state.Folders)
        {
            if (folder.Aside is not null && ServerFileLocator.Contains(folder.Original, path))
            {
                return Path.Combine(folder.Aside, Path.GetRelativePath(folder.Original, path));
            }
        }
        return null;
    }

    /// <summary>
    /// Moves an unchanged file back from the folder kept aside to where the restore puts it, rather than downloading it.
    /// The move is noted first, so that a rollback moves it back again.
    /// </summary>
    /// <param name="previous">The file kept aside, from <see cref="FindPrevious"/>.</param>
    /// <param name="path">Where the restore puts it.</param>
    /// <param name="cancellationToken">Aborts the wait for the list of moved files.</param>
    public async Task ReuseAsync(string previous, string path, CancellationToken cancellationToken)
    {
        await reuseLock.WaitAsync(cancellationToken);
        try
        {
            await using StreamWriter writer = new(ReusedListPath, append: true);
            await writer.WriteLineAsync(path);
            await writer.FlushAsync(CancellationToken.None);
        }
        finally
        {
            reuseLock.Release();
        }
        File.Move(previous, path, overwrite: true);
    }

    /// <summary>
    /// Puts the folders back as they were: moves the reused files back, removes what the restore wrote in place of the
    /// folders, then renames them back. Safe to run again after an interruption.
    /// </summary>
    public void RollBack()
    {
        if (File.Exists(ReusedListPath))
        {
            foreach (string path in File.ReadLines(ReusedListPath))
            {
                if (path.Length == 0)
                {
                    continue;
                }

                // A folder already renamed back by an earlier rollback holds the file where it was.
                SetAsideFolder? folder = state.Folders.FirstOrDefault(
                    f => f.Aside is not null && ServerFileLocator.Contains(f.Original, path));
                if (folder?.Aside is { } aside
                    && Directory.Exists(aside)
                    && File.Exists(path)
                    && FindPrevious(path) is { } previous
                    && !File.Exists(previous))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    File.Move(path, previous);
                }
            }
        }

        for (int i = state.Folders.Count - 1; i >= 0; i--)
        {
            SetAsideFolder folder = state.Folders[i];
            if (folder.Aside is not null && !Directory.Exists(folder.Aside))
            {
                // Already put back by an earlier rollback.
                continue;
            }
            if (Directory.Exists(folder.Original))
            {
                DeleteDirectory(folder.Original);
            }
            if (folder.Aside is not null)
            {
                Directory.Move(folder.Aside, folder.Original);
            }
        }
        File.Delete(ReusedListPath);
        File.Delete(statePath);
    }

    /// <summary>
    /// Deletes the folders kept aside, once the restore succeeded.
    /// </summary>
    /// <returns>The folders that could not be deleted.</returns>
    public IReadOnlyList<string> Commit()
    {
        File.Delete(statePath);
        File.Delete(ReusedListPath);
        List<string> left = [];
        foreach (SetAsideFolder folder in state.Folders)
        {
            if (folder.Aside is null || !Directory.Exists(folder.Aside))
            {
                continue;
            }
            try
            {
                DeleteDirectory(folder.Aside);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                left.Add(folder.Aside);
            }
        }
        return left;
    }

    /// <summary>
    /// Deletes a folder, read-only files included: p4d keeps its archives read-only.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            }))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        Directory.Delete(path, recursive: true);
    }
    
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        string temporaryPath = statePath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                RestoreSetAsideJsonContext.Default.RestoreSetAsideState,
                cancellationToken);
        }
        File.Move(temporaryPath, statePath, overwrite: true);
    }
}

/// <summary>
/// A folder of an instance during a restore.
/// </summary>
/// <param name="Original">Where the folder is.</param>
/// <param name="Aside">Where its previous content was moved, or null when it did not exist.</param>
public sealed record SetAsideFolder(string Original, string? Aside);

internal sealed record RestoreSetAsideState(Guid RunId, bool WasRunning, List<SetAsideFolder> Folders);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RestoreSetAsideState))]
internal sealed partial class RestoreSetAsideJsonContext : JsonSerializerContext;
