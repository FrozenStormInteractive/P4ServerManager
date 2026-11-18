using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using FluentValidation;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Rules shared by the requests that add an instance, whether it is created or imported. Set
/// <see cref="CascadeMode.Stop"/> on the validator: the checks against the database or the machine only run on a
/// well-formed value.
/// </summary>
public static partial class InstanceRuleExtensions
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MaxDescriptionLength = 512;

    /// <summary>
    /// A well-formed name that no other instance uses, whatever its case.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeNewInstanceName<T>(
        this IRuleBuilder<T, string> rule,
        DaemonDbContext db)
    {
        return rule
            .MustBeInstanceName()
            .MustAsync((name, cancellationToken) =>
                db.Instances.AllAsync(i => i.Name.ToLower() != name.ToLower(), cancellationToken))
            .WithMessage((_, name) => $"An instance named {name} already exists.");
    }

    /// <summary>
    /// A well-formed name whose default server directory, in the servers directory, is missing or empty. Only for a
    /// request without a server directory.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustHaveFreeDefaultServerDirectory<T>(
        this IRuleBuilder<T, string> rule,
        DaemonPaths paths)
    {
        return rule
            .Must(name => IsFreeServerDirectory(paths.GetDefaultServerDirectory(name)))
            .WithMessage((_, name) =>
                $"The folder {paths.GetDefaultServerDirectory(name)} is already used: choose another name, or a server directory.");
    }

    /// <summary>
    /// Whether a server directory can be created there: nothing, or an empty folder.
    /// </summary>
    public static bool IsFreeServerDirectory(string path)
    {
        return !File.Exists(path) && (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any());
    }

    /// <summary>
    /// A well-formed name, whether another instance has it or not.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeInstanceName<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Matches(NamePattern())
            .WithMessage("The name starts with a letter and has up to 32 letters, digits, '-' or '_'.");
    }

    /// <summary>
    /// A well-formed p4d server ID. It follows the rule of the names, which a new server takes as its server ID.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeServerId<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Matches(NamePattern())
            .WithMessage("The server ID starts with a letter and has up to 32 letters, digits, '-' or '_'.");
    }

    /// <summary>
    /// A well-formed P4 user name, such as the one of a super user.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeUserName<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Matches(UserNamePattern())
            .WithMessage("The user name starts with a letter and has up to 64 letters, digits, '.', '-' or '_'.");
    }

    public static IRuleBuilderOptions<T, string?> MustBeInstanceDescription<T>(this IRuleBuilder<T, string?> rule)
    {
        return rule
            .MaximumLength(MaxDescriptionLength)
            .WithMessage("The description has at most {MaxLength} characters.");
    }

    /// <summary>
    /// A port in range.
    /// </summary>
    public static IRuleBuilderOptions<T, int> MustBeInstancePort<T>(this IRuleBuilder<T, int> rule)
    {
        return rule
            .InclusiveBetween(MinPort, MaxPort)
            .WithMessage("The port is between {From} and {To}.");
    }

    /// <summary>
    /// A port in range, that no other instance uses and that is free on the machine.
    /// </summary>
    public static IRuleBuilderOptions<T, int> MustBeFreeInstancePort<T>(this IRuleBuilder<T, int> rule, DaemonDbContext db)
    {
        return rule
            .MustBeInstancePort()
            .MustAsync((port, cancellationToken) => db.Instances.AllAsync(i => i.Port != port, cancellationToken))
            .WithMessage((_, port) => $"Another instance uses port {port}.")
            .Must(IsPortFreeOnHost)
            .WithMessage((_, port) => $"Port {port} is already in use on this machine.");
    }

    /// <summary>
    /// A release such as <c>r25.1</c>, with a build in the archive or published on the Perforce FTP. The archive is
    /// looked at first: an archived release is available while the Perforce FTP cannot be read.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeAvailableRelease<T>(
        this IRuleBuilder<T, string> rule,
        IP4ServerReleaseCatalog releaseCatalog,
        DaemonDbContext db)
    {
        return rule
            .MustAsync(async (release, cancellationToken) =>
                P4ServerRelease.TryParse(release, out P4ServerRelease parsed)
                && (await db.Builds.AnyAsync(b => b.Release == parsed.Name, cancellationToken)
                    || (await releaseCatalog.GetReleasesAsync(cancellationToken)).Contains(parsed)))
            .WithMessage((_, release) => $"{release} is not an available p4d release.");
    }

    /// <summary>
    /// A build number of the archive, in the release that <paramref name="getRelease"/> reads from the request.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeArchivedBuild<T>(
        this IRuleBuilder<T, string> rule,
        DaemonDbContext db,
        Func<T, string> getRelease)
    {
        return rule
            .MustAsync((request, build, cancellationToken) =>
            {
                string release = getRelease(request);
                return db.Builds.AnyAsync(b => b.Release == release && b.BuildNumber == build, cancellationToken);
            })
            .WithMessage((request, build) => $"Build {build} of {getRelease(request)} is not in the archive.");
    }

    /// <summary>
    /// Whether a program of the machine could listen on the port: nothing listens on it yet.
    /// </summary>
    public static bool IsPortFreeOnHost(int port)
    {
        try
        {
            TcpListener listener = new(IPAddress.Any, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex UserNamePattern();
}
