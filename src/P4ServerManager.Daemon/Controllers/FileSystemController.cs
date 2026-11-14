using Microsoft.AspNetCore.Mvc;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Services.FileSystem;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The folders of the machine of the daemon, to pick a path, such as the folder of a server, from the web UI. Only the
/// folders the daemon may read are shown, and never the files. Nothing is ever created here: the service that receives
/// a picked path creates its folder when it needs one.
/// </summary>
[ApiController]
[Route("api/filesystem")]
[Produces("application/json")]
[Tags("File system")]
public sealed class FileSystemController : ControllerBase
{
    /// <summary>
    /// Lists the subfolders of a folder, with its ancestors and the roots of the file system. Each folder tells whether the
    /// daemon may create files and folders in it.
    /// </summary>
    /// <param name="path">
    /// Absolute path of the folder to list. Defaults to the first root of the file system: <c>/</c>, or the system drive
    /// on Windows.
    /// </param>
    /// <response code="200">The folder and its subfolders.</response>
    /// <response code="400">The path is not absolute.</response>
    /// <response code="403">The daemon may not read the folder.</response>
    /// <response code="404">The folder does not exist, or it is a file.</response>
    [HttpGet("directories")]
    [ProducesResponseType<DirectoryListingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public ActionResult<DirectoryListingDto> GetDirectories([FromQuery] string? path)
    {
        try
        {
            return DirectoryBrowser.List(string.IsNullOrEmpty(path) ? null : path).ToDto();
        }
        catch (ArgumentException)
        {
            ModelState.AddModelError(nameof(path), "The folder is not a valid absolute path.");
            return ValidationProblem();
        }
        catch (DirectoryNotFoundException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return Problem("The daemon may not read this folder.", statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
