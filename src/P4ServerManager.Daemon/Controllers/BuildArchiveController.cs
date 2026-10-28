using Microsoft.AspNetCore.Mvc;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// How the daemon fills the archive of p4d builds from the Perforce FTP, and what it is doing.
/// </summary>
[ApiController]
[Route("api/p4d/archive")]
[Produces("application/json")]
[Tags("p4d")]
public sealed class BuildArchiveController(
    DaemonDbContext db,
    BuildArchiveWorker worker,
    BuildNotifier notifier) : ControllerBase
{
    /// <summary>
    /// Gets the policy of the archive.
    /// </summary>
    /// <response code="200">The policy.</response>
    [HttpGet("policy")]
    [ProducesResponseType<BuildArchivePolicyDto>(StatusCodes.Status200OK)]
    public async Task<BuildArchivePolicyDto> GetPolicy(CancellationToken cancellationToken)
    {
        BuildArchivePolicy policy = await db.GetBuildArchivePolicyAsync(cancellationToken);
        return policy.ToDto();
    }

    /// <summary>
    /// Replaces the policy of the archive. A smaller retention applies at once: the builds beyond it are removed in the
    /// background, except those that instances use and the protected ones.
    /// </summary>
    /// <param name="request">The policy, checked by <see cref="BuildArchivePolicyDtoValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The policy saved.</response>
    /// <response code="400">The policy is not valid. The errors are keyed by field.</response>
    [HttpPut("policy")]
    [ProducesResponseType<BuildArchivePolicyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<BuildArchivePolicyDto> UpdatePolicy(
        BuildArchivePolicyDto request,
        CancellationToken cancellationToken)
    {
        BuildArchivePolicy policy = await db.GetBuildArchivePolicyAsync(cancellationToken);
        policy.AutoDownload = request.AutoDownload;
        policy.CheckIntervalHours = request.CheckIntervalHours;
        policy.Scope = request.Scope;
        policy.Releases = request.Releases.Distinct().Order().ToList();
        policy.KeepLatestPerRelease = request.KeepLatestPerRelease;
        await db.SaveChangesAsync(cancellationToken);

        worker.RequestRetention();
        await notifier.ArchiveStatusChangedAsync(await worker.GetStatusAsync(cancellationToken));
        return policy.ToDto();
    }

    /// <summary>
    /// Gets what the archive worker does, and how its last check of the Perforce FTP went.
    /// </summary>
    /// <response code="200">The status.</response>
    [HttpGet("status")]
    [ProducesResponseType<BuildArchiveStatusDto>(StatusCodes.Status200OK)]
    public Task<BuildArchiveStatusDto> GetStatus(CancellationToken cancellationToken)
    {
        return worker.GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Checks the Perforce FTP for new builds of the releases of the policy now, whether the automatic download is on
    /// or not, then applies the retention. The check runs in the background: follow it with
    /// <c>GET /api/p4d/archive/status</c> or the <c>ArchiveStatusChanged</c> event of the <c>/hubs/builds</c> hub.
    /// </summary>
    /// <response code="202">The check is queued.</response>
    /// <response code="409">A check is already queued or running.</response>
    [HttpPost("check")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public IActionResult Check()
    {
        if (!worker.RequestCheck())
        {
            return Problem(
                "Perforce is already being checked for new builds.",
                statusCode: StatusCodes.Status409Conflict);
        }
        return Accepted();
    }
}
