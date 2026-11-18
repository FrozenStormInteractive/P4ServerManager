using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Recommended P4 configuration
/// </summary>
public static class RecommendedConfiguration
{
    /// <summary>
    /// Security level of a new server: strong passwords, ticket-based authentication, and authenticated service users.
    /// </summary>
    public const int SecurityLevel = 4;

    /// <summary>
    /// Entries of the typemap of a new server: file type, then path pattern.
    /// </summary>
    private static readonly string[] TypeMapEntries =
    [
        "text //....asp", "text //....cnf", "text //....css", "text //....htm",
        "text //....html", "text //....inc", "text //....js", "text+w //....log",
        "text+w //....ini", "text+w //....pdm", "binary+Fl //....zip", "binary+Fl //....bz2",
        "binary+Fl //....rar", "binary+Fl //....gz", "binary+Fl //....avi", "binary+Fl //....jpg",
        "binary+Fl //....jpeg", "binary+Fl //....mpg", "binary+Fl //....gif", "binary+Fl //....tif",
        "binary+Fl //....mov", "binary+Fl //....jar", "binary+l //....ico", "binary+l //....exp",
        "binary+l //....btr", "binary+l //....bmp", "binary+l //....doc", "binary+l //....dot",
        "binary+l //....xls", "binary+l //....ppt", "binary+l //....pdf", "binary+l //....tar",
        "binary+l //....exe", "binary+l //....dll", "binary+l //....lib", "binary+l //....bin",
        "binary+l //....class", "binary+l //....war", "binary+l //....ear", "binary+l //....so",
        "binary+l //....rpt", "binary+l //....cfm", "binary+l //....ma", "binary+l //....mb",
        "binary+l //....pac", "binary+l //....m4a", "binary+l //....mp4", "binary+l //....aac",
        "binary+l //....wma", "binary+l //....docx", "binary+l //....pptx", "binary+l //....xlsx",
        "binary+l //....png", "binary+l //....raw", "binary+l //....odt", "binary+l //....ods",
        "binary+l //....odg", "binary+l //....odp", "binary+l //....otg", "binary+l //....ots",
        "binary+l //....ott", "binary+l //....psd", "binary+l //....sxw",
    ];

    /// <summary>
    /// Configurables of a new server, in the order the script sets them.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> GetConfigurables(P4ServerInstance instance)
    {
        string logs = $"../{ServerLayout.LogsFolder}";
        List<(string Name, string Value)> configurables =
        [
            ("run.users.authorize", "1"),
            ("dm.user.noautocreate", "2"),
            ("dm.user.resetpassword", "1"),
            ("dm.info.hide", "1"),
            ("dm.user.hideinvalid", "1"),
            ("server.start.unlicensed", "1"),

            ("server.maxcommands", "2500"),
            ("net.backlog", "2048"),
            ("net.autotune", "1"),
            ("db.monitor.shared", "4096"),
            ("db.reorg.disable", "1"),
            ("lbr.autocompress", "1"),

            ("filesys.bufsize", "1M"),
            ("filesys.checklinks", "2"),
            ("server.commandlimits", "2"),
            ("rejectList", "P4EXP,version=2014.2"),

            ("rpl.checksum.auto", "1"),
            ("rpl.checksum.change", "2"),
            ("rpl.checksum.table", "1"),

            ("proxy.monitor.level", "1"),
            ("monitor", "1"),
            ("server", "3"),

            ("filesys.P4ROOT.min", "2G"),
            ("filesys.depot.min", "2G"),
            ("filesys.P4JOURNAL.min", "2G"),
            (ServerConfigurables.DepotRoot, $"../{ServerLayout.ArchivesFolder}"),
            (ServerConfigurables.JournalPrefix, $"../{ServerLayout.JournalsFolder}/{instance.Name}"),
        ];

        // Structured logs.
        string[] serverLogs = ["commands", "errors", "events", "integrity", "auth"];
        for (int i = 0; i < serverLogs.Length; i++)
        {
            configurables.Add(($"{ServerConfigurables.StructuredLogFilePrefix}{i + 1}", $"{logs}/{serverLogs[i]}.csv"));
            configurables.Add(($"serverlog.retain.{i + 1}", "10"));
        }
        return configurables;
    }

    /// <summary>
    /// Server spec of a server, which makes it a commit server and gives its address.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="serverId">Server ID of p4d, which names the spec.</param>
    public static string GetServerSpec(P4ServerInstance instance, string serverId)
    {
        return $"ServerID:\t{serverId}\n\n"
            + "Type:\tserver\n\n"
            + "Services:\tcommit-server\n\n"
            + $"Address:\t{P4ServerLauncher.GetListenAddress(instance)}\n\n"
            + "Description:\n\tCreated by P4 Server Manager.\n";
    }

    /// <summary>
    /// Typemap of a new server, as <c>p4 typemap -i</c> reads it.
    /// </summary>
    public static string GetTypeMap()
    {
        return "TypeMap:\n" + string.Concat(TypeMapEntries.Select(entry => $"\t{entry}\n"));
    }
}
