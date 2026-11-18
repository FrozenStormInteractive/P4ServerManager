namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Names of the p4d configurables the daemon sets in one place and reads in another. A misspelled name is not an error
/// for p4d nor for a lookup: the value is just missing, and a backup or a deletion then misses files. The names set only
/// once, such as those of <see cref="Instances.Provisioning.RecommendedConfiguration"/>, stay where they are set.
/// </summary>
public static class ServerConfigurables
{
    /// <summary>
    /// The live journal, which p4d writes to without a pause. Its name does not follow <see cref="JournalPrefix"/>.
    /// Defaults to <c>journal</c> in the root.
    /// </summary>
    public const string Journal = "P4JOURNAL";

    /// <summary>
    /// The log of p4d. Defaults to <c>log</c> in the root.
    /// </summary>
    public const string Log = "P4LOG";

    /// <summary>
    /// Folder and start of the names of the checkpoints and the rotated journals, such as <c>../journals/main</c> for
    /// <c>main.ckp.12.gz</c> and <c>main.jnl.11</c>. Without it, they are <c>checkpoint.12</c> and <c>journal.11</c> in
    /// the root.
    /// </summary>
    public const string JournalPrefix = "journalPrefix";

    /// <summary>
    /// Folder that holds the archives of the depots whose map is relative. Defaults to the root.
    /// </summary>
    public const string DepotRoot = "server.depot.root";

    /// <summary>
    /// Start of the names of the structured logs, numbered from 1: <c>serverlog.file.1</c>, <c>serverlog.file.2</c>...
    /// </summary>
    public const string StructuredLogFilePrefix = "serverlog.file.";
}
