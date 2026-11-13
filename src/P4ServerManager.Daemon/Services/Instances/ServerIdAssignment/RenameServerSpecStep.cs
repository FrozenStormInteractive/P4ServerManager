using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

/// <summary>
/// Gives the server spec the new server ID, as the super user of the running p4d. The spec of the old server ID is
/// saved under the new one, then deleted, so its type, services, address and description are kept: p4 itself gives it
/// the new server ID, with <c>--field</c>, so the daemon never edits the text of the spec. A server without a
/// spec of its old server ID gets the recommended one. A spec of the new server ID that exists already is kept.
/// </summary>
public sealed class RenameServerSpecStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<RenameServerSpecStep> logger) : IOperationStep<IServerSpecRenameContext>
{
    public string Key => "rename-server-spec";

    public string Title => "Rename the server spec";

    public async Task<string?> RunAsync(
        IServerSpecRenameContext context,
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
            IReadOnlyList<string> serverIds = (await session.RunTaggedAsync(["servers"], cancellationToken))
                .Select(record => record.GetValueOrDefault("ServerID"))
                .OfType<string>()
                .ToList();
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
                // The old spec as it is, to save it again if p4d refuses the new one.
                string oldSpec = (await session.RunAsync(["server", "-o", oldId!], cancellationToken: cancellationToken))
                    .StandardOutput;
                string newSpec = (await session.RunAsync(
                        ["--field", $"ServerID={newId}", "server", "-o", oldId!],
                        cancellationToken: cancellationToken))
                    .StandardOutput;
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

/// <summary>
/// Context of an operation whose server spec follows a new server ID, with the <see cref="RenameServerSpecStep"/>.
/// </summary>
public interface IServerSpecRenameContext : ISuperUserContext
{
    /// <summary>
    /// Server ID before the change, or null when the server had none.
    /// </summary>
    string? OldServerId { get; }

    string NewServerId { get; }
}

