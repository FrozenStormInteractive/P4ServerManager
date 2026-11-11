using System.Globalization;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.PortChange;

/// <summary>
/// Saves the new port of the stopped instance, so that the next start of p4d listens on it. The unique index on the
/// port refuses it when another instance took it in the meantime.
/// </summary>
public sealed class SetPortStep(InstanceNotifier notifier) : IOperationStep<PortChangeContext>
{
    public string Key => "set-port";

    public string Title => "Change the port";

    public async Task<string?> RunAsync(
        PortChangeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        instance.Port = context.NewPort;
        try
        {
            // Not canceled once begun: the daemon would no longer know which port p4d listens on.
            await context.Db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            instance.Port = context.OldPort;
            context.Db.Entry(instance).Property(i => i.Port).IsModified = false;
            throw new InstanceOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Another instance uses port {context.NewPort}."));
        }

        await notifier.InstanceChangedAsync(instance);
        return string.Create(CultureInfo.InvariantCulture, $"{context.OldPort} to {context.NewPort}");
    }
}
