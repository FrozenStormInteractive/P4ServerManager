using System.Text.Json;
using System.Text.Json.Serialization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Json;

/// <summary>
/// JSON of the API, generated at build time so that the CLI stays compatible with Native AOT. Same conventions as the
/// daemon: camelCase properties and enums written as their names.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<InstanceDto>))]
[JsonSerializable(typeof(CreateInstanceRequest))]
[JsonSerializable(typeof(UpdateInstanceRequest))]
[JsonSerializable(typeof(UpgradeInstanceRequest))]
[JsonSerializable(typeof(OperationSnapshot))]
[JsonSerializable(typeof(InstanceSelectionRequest))]
[JsonSerializable(typeof(IReadOnlyList<InstanceActionResult>))]
[JsonSerializable(typeof(IReadOnlyList<BuildDto>))]
[JsonSerializable(typeof(DownloadBuildRequest))]
[JsonSerializable(typeof(UpdateBuildRequest))]
[JsonSerializable(typeof(BuildArchivePolicyDto))]
[JsonSerializable(typeof(BuildArchiveStatusDto))]
[JsonSerializable(typeof(StartHealthCheckRequest))]
[JsonSerializable(typeof(IReadOnlyList<HealthCheckRunDto>))]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
