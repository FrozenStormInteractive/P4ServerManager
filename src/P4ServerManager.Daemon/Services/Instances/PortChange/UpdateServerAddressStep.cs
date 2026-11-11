using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.PortChange;

/// <summary>
/// Moves the address of the server spec to the new port, as the super user of the running p4d. Only the port changes:
/// the protocol and the host of the address are kept. A server without a server ID or a spec of it, or whose address
/// does not name the old port, is left as it is.
/// </summary>
public sealed class UpdateServerAddressStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<UpdateServerAddressStep> logger) : IOperationStep<PortChangeContext>
{
    public string Key => "update-server-address";

    public string Title => "Update the address of the server spec";

    public async Task<string?> RunAsync(
        PortChangeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string? serverId = await ServerLayout.TryReadServerIdAsync(instance.RootPath, cancellationToken);
        if (serverId is null)
        {
            return "No server ID: no server spec to update";
        }

        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context,
            cancellationToken);

        return await session.RunLoggedInAsync<string?>(async () =>
        {
            // p4d compares the spec names as it compares the file names. The list gives the address of each spec.
            StringComparer comparer = instance.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            IReadOnlyDictionary<string, string>? spec = (await session.RunTaggedAsync(["servers"], cancellationToken))
                .FirstOrDefault(record => record.TryGetValue("ServerID", out string? id) && comparer.Equals(id, serverId));
            if (spec is null)
            {
                return $"No server spec {serverId}";
            }

            string? address = spec.GetValueOrDefault("Address")?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                return $"Spec {serverId} has no address";
            }

            string? newAddress = ServerAddressParser.ChangePort(address, context.OldPort, context.NewPort);
            if (newAddress is null)
            {
                return $"Address {address} kept: it does not name port {context.OldPort}";
            }

            // p4 itself changes the field: the daemon never edits the text of the spec.
            string newSpec = (await session.RunAsync(
                    ["--field", $"Address={newAddress}", "server", "-o", spec["ServerID"]],
                    cancellationToken: cancellationToken))
                .StandardOutput;
            await session.RunAsync(["server", "-i"], newSpec, cancellationToken);
            return $"Address {address} to {newAddress}";
        }, cancellationToken);
    }
}
