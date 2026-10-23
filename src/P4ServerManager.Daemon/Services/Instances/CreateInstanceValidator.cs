using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks a <see cref="CreateInstanceRequest"/>. The error keys are the JSON names of the fields.
/// </summary>
public sealed partial class CreateInstanceValidator(DaemonDbContext db, IP4ServerReleaseCatalog releaseCatalog)
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MinPasswordLength = 8;
    public const int MaxDescriptionLength = 512;

    /// <returns>The errors by field, empty when the request is valid.</returns>
    public async Task<Dictionary<string, string[]>> ValidateAsync(
        CreateInstanceRequest request,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]> errors = [];

        if (!NamePattern().IsMatch(request.Name))
        {
            errors["name"] =
            [
                "The name starts with a letter and has up to 32 letters, digits, '-' or '_'.",
            ];
        }
        else if (await db.Instances.AnyAsync(i => i.Name.ToLower() == request.Name.ToLower(), cancellationToken))
        {
            errors["name"] = [$"An instance named {request.Name} already exists."];
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"The description has at most {MaxDescriptionLength} characters."];
        }

        if (request.Port is < MinPort or > MaxPort)
        {
            errors["port"] = [$"The port is between {MinPort} and {MaxPort}."];
        }
        else if (await db.Instances.AnyAsync(i => i.Port == request.Port, cancellationToken))
        {
            errors["port"] = [$"Another instance uses port {request.Port}."];
        }
        else if (!IsPortFree(request.Port))
        {
            errors["port"] = [$"Port {request.Port} is already in use on this machine."];
        }

        if (!P4ServerRelease.TryParse(request.Release, out P4ServerRelease release)
            || !(await releaseCatalog.GetReleasesAsync(cancellationToken)).Contains(release))
        {
            errors["release"] = [$"{request.Release} is not an available p4d release."];
        }

        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            if (!Path.IsPathFullyQualified(request.RootPath))
            {
                errors["rootPath"] = ["The root directory is an absolute path."];
            }
            else if (File.Exists(request.RootPath))
            {
                errors["rootPath"] = ["The root directory is a file."];
            }
            else if (Directory.Exists(request.RootPath)
                && Directory.EnumerateFileSystemEntries(request.RootPath).Any())
            {
                errors["rootPath"] = ["The root directory is not empty."];
            }
        }

        // MVC has already rejected a request without a super user: the member is not nullable.
        if (!UserNamePattern().IsMatch(request.SuperUser.Name))
        {
            errors["superUser.name"] =
            [
                "The user name starts with a letter and has up to 64 letters, digits, '.', '-' or '_'.",
            ];
        }
        if (request.SuperUser.Password is not { Length: >= MinPasswordLength })
        {
            errors["superUser.password"] = [$"The password has at least {MinPasswordLength} characters."];
        }

        return errors;
    }

    private static bool IsPortFree(int port)
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
