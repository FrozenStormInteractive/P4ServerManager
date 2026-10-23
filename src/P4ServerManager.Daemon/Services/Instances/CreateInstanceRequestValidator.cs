using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using FluentValidation;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks a <see cref="CreateInstanceRequest"/> against the other instances, the p4d catalog and the machine.
/// </summary>
public sealed partial class CreateInstanceRequestValidator : AbstractValidator<CreateInstanceRequest>
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MaxDescriptionLength = 512;

    private readonly DaemonDbContext _db;
    private readonly IP4ServerReleaseCatalog _releaseCatalog;

    public CreateInstanceRequestValidator(
        DaemonDbContext db,
        IP4ServerReleaseCatalog releaseCatalog,
        IValidator<SuperUserRequest> superUserValidator)
    {
        _db = db;
        _releaseCatalog = releaseCatalog;

        // One error per field: the checks against the database or the machine only run on a well-formed value.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name)
            .Matches(NamePattern())
            .WithMessage("The name starts with a letter and has up to 32 letters, digits, '-' or '_'.")
            .MustAsync(BeUnusedNameAsync)
            .WithMessage(r => $"An instance named {r.Name} already exists.");

        RuleFor(r => r.Description)
            .MaximumLength(MaxDescriptionLength)
            .WithMessage("The description has at most {MaxLength} characters.");

        RuleFor(r => r.Port)
            .InclusiveBetween(MinPort, MaxPort)
            .WithMessage("The port is between {From} and {To}.")
            .MustAsync(BeUnusedPortAsync)
            .WithMessage(r => $"Another instance uses port {r.Port}.")
            .Must(BeFreeOnHost)
            .WithMessage(r => $"Port {r.Port} is already in use on this machine.");

        RuleFor(r => r.Release)
            .MustAsync(BeAvailableReleaseAsync)
            .WithMessage(r => $"{r.Release} is not an available p4d release.");

        RuleFor(r => r.RootPath!)
            .Must(Path.IsPathFullyQualified)
            .WithMessage("The root directory is an absolute path.")
            .Must(path => !File.Exists(path))
            .WithMessage("The root directory is a file.")
            .Must(path => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            .WithMessage("The root directory is not empty.")
            .When(r => !string.IsNullOrWhiteSpace(r.RootPath));

        RuleFor(r => r.SuperUser).SetValidator(superUserValidator);
    }

    private async Task<bool> BeUnusedNameAsync(string name, CancellationToken cancellationToken)
    {
        return !await _db.Instances.AnyAsync(i => i.Name.ToLower() == name.ToLower(), cancellationToken);
    }

    private async Task<bool> BeUnusedPortAsync(int port, CancellationToken cancellationToken)
    {
        return !await _db.Instances.AnyAsync(i => i.Port == port, cancellationToken);
    }

    private async Task<bool> BeAvailableReleaseAsync(string release, CancellationToken cancellationToken)
    {
        return P4ServerRelease.TryParse(release, out P4ServerRelease parsed)
            && (await _releaseCatalog.GetReleasesAsync(cancellationToken)).Contains(parsed);
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

/// <summary>
/// Checks the super user of a new instance.
/// </summary>
public sealed partial class SuperUserRequestValidator : AbstractValidator<SuperUserRequest>
{
    public const int MinPasswordLength = 8;

    public SuperUserRequestValidator()
    {
        RuleFor(u => u.Name)
            .Matches(UserNamePattern())
            .WithMessage("The user name starts with a letter and has up to 64 letters, digits, '.', '-' or '_'.");

        RuleFor(u => u.Password)
            .MinimumLength(MinPasswordLength)
            .WithMessage("The password has at least {MinLength} characters.");
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex UserNamePattern();
}
