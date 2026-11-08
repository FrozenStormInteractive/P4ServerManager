using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the spec and unload depots, the server spec and the typemap.
/// </summary>
public sealed class CreateSpecsStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<CreateSpecsStep> logger) : IOperationStep<ISuperUserContext>
{
    public string Key => "create-specs";

    public string Title => "Create the depots, the server spec and the typemap";

    public async Task<string?> RunAsync(
        ISuperUserContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context,
            cancellationToken);

        return await session.RunLoggedInAsync<string?>(async () =>
        {
            await CreateDepotAsync(session, "spec", cancellationToken);
            await session.RunAsync(["admin", "updatespecdepot", "-a"], cancellationToken: cancellationToken);
            await CreateDepotAsync(session, "unload", cancellationToken);

            await session.RunAsync(
                ["server", "-i"],
                RecommendedConfiguration.GetServerSpec(context.Instance),
                cancellationToken);
            await session.RunAsync(["typemap", "-i"], RecommendedConfiguration.GetTypeMap(), cancellationToken);
            return "Depots spec and unload, commit server spec, typemap";
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a depot of the type of its name, with the defaults of p4d.
    /// </summary>
    private static async Task CreateDepotAsync(P4AdminSession session, string name, CancellationToken cancellationToken)
    {
        ProcessResult spec = await session.RunAsync(
            ["depot", "-o", "-t", name, name],
            cancellationToken: cancellationToken);
        await session.RunAsync(["depot", "-i"], spec.StandardOutput, cancellationToken);
    }
}
