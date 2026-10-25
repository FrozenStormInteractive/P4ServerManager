using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Import;
using P4ServerManager.Daemon.Services.Instances.Provisioning;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The server profiles of the HelixCoreServer Synology package (p4dctl-ng), which the Synology package of the daemon
/// copies to <c>import/HelixCoreServer</c> in the data directory. Importing one turns its server into an instance.
/// </summary>
[ApiController]
[Route("api/legacy-profiles")]
[Produces("application/json")]
[Tags("Legacy profiles")]
public sealed class LegacyProfilesController(
    DaemonDbContext db,
    LegacyProfileStore store,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
    TimeProvider timeProvider) : ControllerBase
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Lists the profiles waiting to be imported, with what prevents their import.
    /// </summary>
    /// <response code="200">The profiles, by file name. A profile already imported gives its instance.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LegacyProfileDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<LegacyProfileDto>> GetLegacyProfiles(CancellationToken cancellationToken)
    {
        IReadOnlyList<LegacyProfile> profiles = await store.ListAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            return [];
        }

        Dictionary<string, Guid> instancesByRoot = new(PathComparer);
        foreach (var instance in await db.Instances.Select(i => new { i.Id, i.RootPath }).ToListAsync(cancellationToken))
        {
            instancesByRoot.TryAdd(instance.RootPath, instance.Id);
        }

        return profiles
            .Select(profile => LegacyProfileDto.From(
                profile,
                GetFullRootPath(profile) is { } root && instancesByRoot.TryGetValue(root, out Guid id) ? id : null))
            .ToList();
    }

    /// <summary>
    /// Imports the server of a profile as an instance that keeps its root. The import runs in the background: it gets
    /// the p4d build, saves a checkpoint of the database to the folder of the instance, reads its Unicode mode,
    /// upgrades it to the build, then starts p4d. Follow it with <c>GET /api/instances/{id}/operation</c> or the
    /// <c>OperationProgress</c> event of the <c>/hubs/instances</c> hub. The profile and the HelixCoreServer package are
    /// left untouched; the server of the profile must be stopped first.
    /// </summary>
    /// <param name="id">Identifier of the profile: its file name.</param>
    /// <param name="request">
    /// Settings of the instance, checked by <see cref="ImportLegacyProfileRequestValidator"/>.
    /// </param>
    /// <param name="queue">Queue of the provisionings.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="202">The instance is created and its import has started.</response>
    /// <response code="400">The settings are not valid. The errors are keyed by field.</response>
    /// <response code="404">No profile has this identifier.</response>
    /// <response code="409">The profile cannot be imported, or an instance already uses its root.</response>
    /// <response code="502">The list of p4d releases could not be read from the Perforce FTP.</response>
    [HttpPost("{id}/import")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> ImportLegacyProfile(
        string id,
        ImportLegacyProfileRequest request,
        [FromServices] ProvisioningQueue queue,
        CancellationToken cancellationToken)
    {
        LegacyProfile? profile = await store.FindAsync(id, cancellationToken);
        if (profile is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        if (!profile.CanImport)
        {
            return Problem(string.Join(' ', profile.Problems), statusCode: StatusCodes.Status409Conflict);
        }

        string rootPath = GetFullRootPath(profile)!;
        List<string> roots = await db.Instances.Select(i => i.RootPath).ToListAsync(cancellationToken);
        if (roots.Contains(rootPath, PathComparer))
        {
            return Problem(
                $"An instance already uses the root {rootPath}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        Guid instanceId = Guid.NewGuid();
        P4ServerInstance instance = new()
        {
            Id = instanceId,
            Name = request.Name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Port = request.Port,
            RootPath = rootPath,
            Release = request.Release,
            CaseSensitive = request.CaseSensitive,
            // Read from the database by the import.
            Unicode = false,
            AutoStart = request.AutoStart,
            State = P4ServerInstanceState.Provisioning,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Instances.Add(instance);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request took the name or the port in the meantime.
            return Problem(
                "Another instance already uses this name or port.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await queue.EnqueueAsync(new ProvisioningRequest(instanceId, ProvisioningKind.Import), cancellationToken);
        await notifier.InstanceChangedAsync(instance);

        return AcceptedAtAction(
            nameof(InstancesController.GetInstance),
            "Instances",
            new { id = instanceId },
            InstanceDto.From(instance, runtimeRegistry.Get(instanceId)));
    }

    /// <summary>
    /// Dismisses a profile: it is moved to <c>import/HelixCoreServer/dismissed</c> and no longer listed. Its server is
    /// left untouched.
    /// </summary>
    /// <param name="id">Identifier of the profile: its file name.</param>
    /// <response code="204">The profile is dismissed.</response>
    /// <response code="404">No profile has this identifier.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public ActionResult DismissLegacyProfile(string id)
    {
        return store.Dismiss(id) ? NoContent() : Problem(statusCode: StatusCodes.Status404NotFound);
    }

    private static string? GetFullRootPath(LegacyProfile profile)
    {
        return profile.RootPath is { } root && Path.IsPathFullyQualified(root) ? Path.GetFullPath(root) : null;
    }
}
