namespace P4ServerManager.Contracts;

/// <summary>
/// Where a new instance would keep its server, asked before it is created. Read from the query string.
/// </summary>
public sealed record ServerDirectoryPreviewRequest
{
    /// <summary>
    /// Name of the new instance. Required without a server directory: the default server directory is named after it.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Absolute folder of the server, as the request that creates the instance would give it. Missing for the default
    /// server directory.
    /// </summary>
    public string? ServerDirectory { get; init; }
}

/// <summary>
/// The server directory a new instance would get, with what the user should know about it before creating it.
/// </summary>
/// <param name="Path">Absolute path of the server directory, with '/' as the separator on every platform.</param>
/// <param name="IsDefault">True when it is the default server directory, named after the instance.</param>
/// <param name="CaseVariant">
/// An existing file or folder next to it whose name differs by its case only, such as <c>main</c> for <c>Main</c>, or
/// null. The creation is not refused: both folders can live side by side, but they are easy to mix up, especially from a
/// client that ignores the case such as Windows over SMB.
/// </param>
public sealed record ServerDirectoryPreviewDto(string Path, bool IsDefault, string? CaseVariant);
