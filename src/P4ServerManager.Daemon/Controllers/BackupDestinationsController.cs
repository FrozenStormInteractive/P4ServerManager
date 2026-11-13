using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The places the instances back up to, configured once for the daemon. Each destination is an encrypted backup
/// repository on a storage: a local folder, an Amazon S3 or S3-compatible bucket, a Google Cloud Storage bucket, an
/// Azure Blob Storage container or an FTP folder. Each instance chooses its own destinations. Secrets are accepted but
/// never returned.
/// </summary>
[ApiController]
[Route("api/backup-destinations")]
[Produces("application/json")]
[Tags("Backups")]
public sealed class BackupDestinationsController(
    DaemonDbContext db,
    BackupDestinationService destinationService) : ControllerBase
{
    /// <summary>
    /// Lists the backup destinations.
    /// </summary>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The destinations, by name.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BackupDestinationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BackupDestinationDto>>> GetDestinations(
        CancellationToken cancellationToken)
    {
        List<BackupDestination> destinations = await db.BackupDestinations
            .AsNoTracking()
            .Include(d => d.Instances)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
        return destinations.Select(d => d.ToDto(destinationService.GetLocation(d))).ToList();
    }

    /// <summary>
    /// Gets a backup destination.
    /// </summary>
    /// <param name="id">Identifier of the destination.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The destination.</response>
    /// <response code="404">No destination has this identifier.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BackupDestinationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BackupDestinationDto>> GetDestination(Guid id, CancellationToken cancellationToken)
    {
        BackupDestination? destination = await FindAsync(id, cancellationToken);
        return destination is null
            ? Problem(statusCode: StatusCodes.Status404NotFound)
            : destination.ToDto(destinationService.GetLocation(destination));
    }

    /// <summary>
    /// Adds a backup destination. A storage that holds no repository gets a new one, encrypted with the passphrase; a
    /// storage that already holds one, such as after a reinstallation, is attached when the passphrase opens it. The
    /// passphrase is the only way to read the backups from another daemon: keep it somewhere else.
    /// </summary>
    /// <param name="request">The destination, checked by <see cref="CreateBackupDestinationRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="201">The destination is added.</response>
    /// <response code="400">The request is not valid.</response>
    /// <response code="409">Another destination has this name.</response>
    /// <response code="422">The storage cannot be reached, or holds a repository the passphrase does not open.</response>
    [HttpPost]
    [ProducesResponseType<BackupDestinationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<ActionResult<BackupDestinationDto>> CreateDestination(
        CreateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            BackupDestination destination = await destinationService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(
                nameof(GetDestination),
                new { id = destination.Id },
                destination.ToDto(destinationService.GetLocation(destination)));
        }
        catch (BackupDestinationConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (BackupDestinationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>
    /// Changes a backup destination. Its kind and its passphrase stay. Moved to a storage that holds no repository, it
    /// gets a new one with the same passphrase: the backups at the previous place stay there.
    /// </summary>
    /// <param name="id">Identifier of the destination.</param>
    /// <param name="request">The destination, checked by <see cref="UpdateBackupDestinationRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The destination is changed.</response>
    /// <response code="400">The request is not valid.</response>
    /// <response code="404">No destination has this identifier.</response>
    /// <response code="409">Another destination has this name.</response>
    /// <response code="422">
    /// The settings are not the ones of the kind of the destination, the storage cannot be reached, or it holds another
    /// repository.
    /// </response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<BackupDestinationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<ActionResult<BackupDestinationDto>> UpdateDestination(
        Guid id,
        UpdateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await destinationService.UpdateAsync(id, request, cancellationToken);
            BackupDestination destination = (await FindAsync(id, cancellationToken))!;
            return destination.ToDto(destinationService.GetLocation(destination));
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (BackupDestinationConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (BackupDestinationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>
    /// Removes a backup destination from the daemon. Its repository and its backups stay on the storage, and can be
    /// attached again with the passphrase.
    /// </summary>
    /// <param name="id">Identifier of the destination.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The destination is removed.</response>
    /// <response code="404">No destination has this identifier.</response>
    /// <response code="409">An instance still backs up to it.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> DeleteDestination(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await destinationService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (BackupDestinationConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Tests a backup destination: writes a small object next to the repository, reads it back and deletes it, then
    /// checks that the repository is still there. Tells what the storage guarantees.
    /// </summary>
    /// <param name="id">Identifier of the destination.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The outcome of the test, successful or not.</response>
    /// <response code="404">No destination has this identifier.</response>
    [HttpPost("{id:guid}/test")]
    [ProducesResponseType<BackupDestinationTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BackupDestinationTestResult>> TestDestination(
        Guid id,
        CancellationToken cancellationToken)
    {
        BackupDestination? destination = await FindAsync(id, cancellationToken);
        return destination is null
            ? Problem(statusCode: StatusCodes.Status404NotFound)
            : await destinationService.TestAsync(destination, cancellationToken);
    }

    private Task<BackupDestination?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        return db.BackupDestinations
            .AsNoTracking()
            .Include(d => d.Instances)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }
}
