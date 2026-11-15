using FluentValidation;
using FluentValidation.Results;

using Microsoft.Extensions.Options;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Options;

namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// Creates the first account, an administrator, from <see cref="DaemonAuthenticationOptions.InitialUserFile"/>, which
/// the install wizard of the package writes. Runs at startup, before the daemon listens, so that nobody can create the first account from
/// the web interface in the meantime.
/// </summary>
public sealed class InitialUserBootstrapper(
    IOptions<DaemonAuthenticationOptions> options,
    UserService userService,
    IServiceScopeFactory scopeFactory,
    ILogger<InitialUserBootstrapper> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? path = options.Value.InitialUserFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            await CreateFromFileAsync(path, cancellationToken);
        }
        finally
        {
            // The file holds a password in clear text: it never outlives the startup, whatever happened.
            File.Delete(path);
        }
    }

    private async Task CreateFromFileAsync(string path, CancellationToken cancellationToken)
    {
        if (await userService.AnyAsync(cancellationToken))
        {
            logger.LogWarning("Ignored the initial account in {Path}: accounts already exist", path);
            return;
        }

        string content = await File.ReadAllTextAsync(path, cancellationToken);
        int newLine = content.IndexOf('\n');
        SetupRequest request = new()
        {
            UserName = (newLine < 0 ? content : content[..newLine]).Trim(),
            Password = newLine < 0 ? string.Empty : content[(newLine + 1)..].TrimEnd('\r', '\n'),
        };

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ValidationResult result = await scope.ServiceProvider
            .GetRequiredService<IValidator<SetupRequest>>()
            .ValidateAsync(request, cancellationToken);
        if (!result.IsValid)
        {
            logger.LogError(
                "Ignored the initial account in {Path}: {Errors} Create the first account from the web interface",
                path,
                string.Join(" ", result.Errors.Select(e => e.ErrorMessage)));
            return;
        }

        await userService.CreateAsync(
            request.UserName,
            request.Password,
            isAdministrator: true,
            firstOnly: true,
            cancellationToken);
    }
}
