using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace P4ServerManager.Daemon.Services.FileSystem;

/// <summary>
/// Whether the daemon may create files and folders in a folder, asked to the operating system without writing anything.
/// </summary>
public static partial class DirectoryAccess
{
    private const int WriteOk = 2;
    private const int ExecuteOk = 1;

    /// <summary>
    /// True when the daemon may create files and folders in the folder. False when it may not, or when it cannot tell.
    /// </summary>
    /// <param name="path">Absolute path of an existing folder.</param>
    public static bool CanCreateEntries(string path)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CanCreateEntriesOnWindows(path) : CanCreateEntriesOnUnix(path);
        }
        catch (SystemException)
        {
            return false;
        }
    }

    // access(2) checks the permissions, the ACLs and a read-only mount for the user of the process. Entering the folder
    // (execute) is needed as well as writing to it, to create anything inside.
    private static bool CanCreateEntriesOnUnix(string path)
    {
        return Access(path, WriteOk | ExecuteOk) == 0;
    }

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);

    // Windows is for development only: the allow and deny rules of the folder for the user and its groups. A deny rule
    // wins over an allow rule.
    [SupportedOSPlatform("windows")]
    private static bool CanCreateEntriesOnWindows(string path)
    {
        DirectoryInfo directory = new(path);
        if (directory.Attributes.HasFlag(FileAttributes.ReadOnly) && directory.Parent is null)
        {
            // A read-only volume, such as a disc.
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        HashSet<SecurityIdentifier> sids = [identity.User!, .. identity.Groups?.OfType<SecurityIdentifier>() ?? []];

        FileSystemRights allowed = 0;
        FileSystemRights denied = 0;
        foreach (FileSystemAccessRule rule in directory.GetAccessControl()
                     .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || !sids.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny)
            {
                denied |= rule.FileSystemRights;
            }
            else
            {
                allowed |= rule.FileSystemRights;
            }
        }

        const FileSystemRights createRights = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;
        return (allowed & createRights) == createRights && (denied & createRights) == 0;
    }
}
