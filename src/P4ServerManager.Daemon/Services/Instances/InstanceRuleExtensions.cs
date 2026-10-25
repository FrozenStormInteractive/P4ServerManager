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
            .Matches(NamePattern())
            .WithMessage("The name starts with a letter and has up to 32 letters, digits, '-' or '_'.")
            .MustAsync((name, cancellationToken) =>
                db.Instances.AllAsync(i => i.Name.ToLower() != name.ToLower(), cancellationToken))
            .WithMessage((_, name) => $"An instance named {name} already exists.");
    }

    public static IRuleBuilderOptions<T, string?> MustBeInstanceDescription<T>(this IRuleBuilder<T, string?> rule)
    {
        return rule
            .MaximumLength(MaxDescriptionLength)
            .WithMessage("The description has at most {MaxLength} characters.");
    }

    /// <summary>
    /// A port in range, that no other instance uses and that is free on the machine.
    /// </summary>
    public static IRuleBuilderOptions<T, int> MustBeFreeInstancePort<T>(this IRuleBuilder<T, int> rule, DaemonDbContext db)
    {
        return rule
            .InclusiveBetween(MinPort, MaxPort)
            .WithMessage("The port is between {From} and {To}.")
            .MustAsync((port, cancellationToken) => db.Instances.AllAsync(i => i.Port != port, cancellationToken))
            .WithMessage((_, port) => $"Another instance uses port {port}.")
            .Must(BeFreeOnHost)
            .WithMessage((_, port) => $"Port {port} is already in use on this machine.");
    }

    /// <summary>
    /// A release of the p4d catalog, such as <c>r25.1</c>.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeAvailableRelease<T>(
        this IRuleBuilder<T, string> rule,
        IP4ServerReleaseCatalog releaseCatalog)
    {
        return rule
            .MustAsync(async (release, cancellationToken) =>
                P4ServerRelease.TryParse(release, out P4ServerRelease parsed)
                && (await releaseCatalog.GetReleasesAsync(cancellationToken)).Contains(parsed))
            .WithMessage((_, release) => $"{release} is not an available p4d release.");
    }

    private static bool BeFreeOnHost(int port)
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
}
