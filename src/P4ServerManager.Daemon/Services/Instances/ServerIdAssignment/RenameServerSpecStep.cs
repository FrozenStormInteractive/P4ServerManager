using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

/// <summary>
/// Gives the server spec the new server ID, as the super user of the running p4d. The spec of the old server ID is
/// saved under the new one, then deleted, so its type, services, address and description are kept. A server without a
/// spec of its old server ID gets the recommended one. A spec of the new server ID that exists already is kept.
/// </summary>
public sealed class RenameServerSpecStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<RenameServerSpecStep> logger) : IOperationStep<ServerIdAssignmentContext>
{
    public string Key => "rename-server-spec";

    public string Title => "Rename the server spec";

    public async Task<string?> RunAsync(
        ServerIdAssignmentContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string? oldId = context.OldServerId;
        string newId = context.NewServerId;

        // p4d compares the spec names as it compares the file names.
        StringComparer comparer = instance.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        bool sameSpec = oldId is not null && comparer.Equals(oldId, newId);

        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context,
            cancellationToken);

        return await session.RunLoggedInAsync<string?>(async () =>
        {
            ProcessResult list = await session.RunAsync(["-ztag", "servers"], cancellationToken: cancellationToken);
            IReadOnlyList<string> serverIds = ServerSpecParser.ParseServerIds(list.StandardOutput);
            bool oldExists = oldId is not null && serverIds.Contains(oldId, comparer);
            bool newExists = !sameSpec && serverIds.Contains(newId, comparer);

            if (newExists)
            {
                if (oldExists)
                {
                    await session.RunAsync(["server", "-d", oldId!], cancellationToken: cancellationToken);
                }
            }
            else if (oldExists)
            {
                string oldSpec = (await session.RunAsync(["server", "-o", oldId!], cancellationToken: cancellationToken))
                    .StandardOutput;
                string newSpec = ServerSpecParser.ReplaceServerId(oldSpec, newId);
                if (sameSpec)
                {
                    await session.RunAsync(["server", "-i"], newSpec, cancellationToken);
                }
                else
                {
                    await ReplaceSpecAsync(session, oldId!, oldSpec, newSpec);
                }
            }
            else
            {
                await session.RunAsync(
                    ["server", "-i"],
                    RecommendedConfiguration.GetServerSpec(instance, newId),
                    cancellationToken);
            }

            return (newExists, oldExists) switch
            {
                (true, true) => $"Spec {newId} kept, spec {oldId} deleted",
                (true, false) => $"Spec {newId} kept",
                (false, true) => $"Spec {oldId} renamed to {newId}",
                (false, false) => $"Spec {newId} created",
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes the old spec, then saves the new one. p4d allows one commit server only, so the old spec cannot stay
    /// while the new one is saved. It is saved again if p4d refuses the new one. Not canceled once begun: the server
    /// would be left without a spec.
    /// </summary>
    private static async Task ReplaceSpecAsync(P4AdminSession session, string oldId, string oldSpec, string newSpec)
    {
        await session.RunAsync(["server", "-d", oldId], cancellationToken: CancellationToken.None);
        try
        {
            await session.RunAsync(["server", "-i"], newSpec, CancellationToken.None);
        }
        catch (InstanceOperationException)
        {
            await session.TryRunAsync(["server", "-i"], oldSpec, CancellationToken.None);
            throw;
        }
    }
}
