namespace P4ServerManager.Contracts;

/// <summary>
/// The folders inside a folder of the machine of the daemon, to pick a path from the web UI. The paths use '/' as the
/// separator on every platform, such as <c>C:/p4</c> on Windows.
/// </summary>
/// <param name="Path">Absolute path of the folder, normalized.</param>
/// <param name="Parent">Folder that holds it, or null for a root.</param>
/// <param name="Writable">Whether the daemon may create files and folders in the folder.</param>
/// <param name="Ancestors">The folders from its root down to the folder itself, to show it as a breadcrumb.</param>
/// <param name="Directories">
/// Its subfolders, by name. Hidden and system folders, such as those whose name starts with a dot, are left out.
/// </param>
/// <param name="Roots">The roots of the file system: <c>/</c>, or the drives on Windows, such as <c>C:/</c>.</param>
public sealed record DirectoryListingDto(
    string Path,
    string? Parent,
    bool Writable,
    IReadOnlyList<DirectoryEntryDto> Ancestors,
    IReadOnlyList<DirectoryEntryDto> Directories,
    IReadOnlyList<DirectoryEntryDto> Roots);

/// <summary>
/// A folder of the machine of the daemon.
/// </summary>
/// <param name="Name">Name of the folder, or its path for a root.</param>
/// <param name="Path">Absolute path of the folder.</param>
/// <param name="Writable">
/// Whether the daemon may create files and folders in it. A folder it may not is only shown, to reach its subfolders.
/// </param>
public sealed record DirectoryEntryDto(string Name, string Path, bool Writable);
