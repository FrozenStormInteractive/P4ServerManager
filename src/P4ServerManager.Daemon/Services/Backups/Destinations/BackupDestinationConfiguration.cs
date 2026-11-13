using System.Text.Json;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// Everything needed to reach the storage of a destination, its secrets included. Lives in memory only.
/// </summary>
public sealed record BackupDestinationConfiguration(
    BackupDestinationKind Kind,
    LocalDestinationSettings? Local,
    S3DestinationSettings? S3,
    GcsDestinationSettings? Gcs,
    AzureBlobDestinationSettings? Azure,
    FtpDestinationSettings? Ftp,
    BackupDestinationCredentials Credentials)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The settings of the kind, as stored: without any secret.
    /// </summary>
    public string SerializeSettings()
    {
        return Kind switch
        {
            BackupDestinationKind.Local => JsonSerializer.Serialize(Required(Local), JsonOptions),
            BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible =>
                JsonSerializer.Serialize(Required(S3), JsonOptions),
            BackupDestinationKind.GoogleCloudStorage => JsonSerializer.Serialize(Required(Gcs), JsonOptions),
            BackupDestinationKind.AzureBlob => JsonSerializer.Serialize(Required(Azure), JsonOptions),
            BackupDestinationKind.Ftp => JsonSerializer.Serialize(Required(Ftp), JsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        };
    }

    public static BackupDestinationConfiguration Deserialize(
        BackupDestinationKind kind,
        string settings,
        BackupDestinationCredentials credentials)
    {
        return kind switch
        {
            BackupDestinationKind.Local => new(kind, Read<LocalDestinationSettings>(settings), null, null, null, null, credentials),
            BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible =>
                new(kind, null, Read<S3DestinationSettings>(settings), null, null, null, credentials),
            BackupDestinationKind.GoogleCloudStorage =>
                new(kind, null, null, Read<GcsDestinationSettings>(settings), null, null, credentials),
            BackupDestinationKind.AzureBlob =>
                new(kind, null, null, null, Read<AzureBlobDestinationSettings>(settings), null, credentials),
            BackupDestinationKind.Ftp => new(kind, null, null, null, null, Read<FtpDestinationSettings>(settings), credentials),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public static string SerializeCredentials(BackupDestinationCredentials credentials)
    {
        return JsonSerializer.Serialize(credentials, JsonOptions);
    }

    public static BackupDestinationCredentials DeserializeCredentials(string json)
    {
        return JsonSerializer.Deserialize<BackupDestinationCredentials>(json, JsonOptions)
            ?? new BackupDestinationCredentials();
    }

    /// <summary>
    /// Whether any secret is set.
    /// </summary>
    public static bool HasAny(BackupDestinationCredentials credentials)
    {
        return credentials.SecretAccessKey is not null
            || credentials.ServiceAccountJson is not null
            || credentials.AccountKey is not null
            || credentials.SasToken is not null
            || credentials.Password is not null;
    }

    private static T Required<T>(T? settings)
        where T : class
    {
        return settings ?? throw new InvalidOperationException($"The destination has no {typeof(T).Name}.");
    }

    private static T Read<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"The stored {typeof(T).Name} cannot be read.");
    }
}
