using Microsoft.AspNetCore.Mvc;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// The files of a p4d build imported by a user: p4d and p4, or the package that holds them. It stays in the daemon:
/// the contracts depend on the BCL only, and the CLI sends the same form.
/// </summary>
public sealed class ImportBuildForm
{
    /// <summary>
    /// The p4d executable for this platform. Sent with <see cref="P4"/>.
    /// </summary>
    [FromForm(Name = "p4d")]
    public IFormFile? P4d { get; init; }

    /// <summary>
    /// The p4 executable of the same release. Sent with <see cref="P4d"/>.
    /// </summary>
    [FromForm(Name = "p4")]
    public IFormFile? P4 { get; init; }

    /// <summary>
    /// The server package of Perforce, <c>helix-core-server.tgz</c>, or any tar archive, compressed with gzip or not,
    /// that holds p4d and p4. Sent alone.
    /// </summary>
    [FromForm(Name = "package")]
    public IFormFile? Package { get; init; }
}
