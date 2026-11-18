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
[JsonSerializable(typeof(ServerDirectoryPreviewDto))]
[JsonSerializable(typeof(UpdateInstanceRequest))]
[JsonSerializable(typeof(SetSuperUserRequest))]
[JsonSerializable(typeof(UpgradeInstanceRequest))]
[JsonSerializable(typeof(ChangeServerIdRequest))]
[JsonSerializable(typeof(ChangePortRequest))]
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
[JsonSerializable(typeof(IReadOnlyList<BackupDestinationDto>))]
[JsonSerializable(typeof(CreateBackupDestinationRequest))]
[JsonSerializable(typeof(UpdateBackupDestinationRequest))]
[JsonSerializable(typeof(BackupDestinationTestResult))]
[JsonSerializable(typeof(InstanceBackupDestinations))]
[JsonSerializable(typeof(StartBackupRequest))]
[JsonSerializable(typeof(VerifyBackupRequest))]
[JsonSerializable(typeof(IReadOnlyList<BackupRunDto>))]
[JsonSerializable(typeof(IReadOnlyList<BackupPointDto>))]
[JsonSerializable(typeof(IReadOnlyList<RestoreRunDto>))]
[JsonSerializable(typeof(RestoreBackupRequest))]
[JsonSerializable(typeof(RestoreInstanceRequest))]
[JsonSerializable(typeof(IReadOnlyList<UserDto>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(SetUserPasswordRequest))]
[JsonSerializable(typeof(SetUserAdministratorRequest))]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
