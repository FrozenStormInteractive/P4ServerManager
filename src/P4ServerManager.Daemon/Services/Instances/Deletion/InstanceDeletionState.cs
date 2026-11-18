using System.Text.Json;
using System.Text.Json.Serialization;

namespace P4ServerManager.Daemon.Services.Instances.Deletion;

/// <summary>
/// A deletion of an instance that has begun, saved in the folder the daemon keeps for the instance before anything is
/// stopped or deleted. That folder goes last, so that a daemon that stops in the middle finds the file at its next start
/// and carries the deletion on, with the same plan: once the root is gone, the configuration of the server could no
/// longer be read, and a depot kept elsewhere would be forgotten.
/// </summary>
/// <param name="Plan">The files of the server to delete, or null when they are kept.</param>
public sealed record InstanceDeletionState(InstanceFilePlan? Plan)
{
    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                this,
                InstanceDeletionJsonContext.Default.InstanceDeletionState,
                cancellationToken);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <returns>The deletion saved at <paramref name="path"/>, or null when there is none.</returns>
    /// <exception cref="JsonException">The file cannot be read.</exception>
    public static async Task<InstanceDeletionState?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
            stream,
            InstanceDeletionJsonContext.Default.InstanceDeletionState,
            cancellationToken);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstanceDeletionState))]
internal sealed partial class InstanceDeletionJsonContext : JsonSerializerContext;
