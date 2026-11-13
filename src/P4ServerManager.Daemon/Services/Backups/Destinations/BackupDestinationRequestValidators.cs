using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// Checks a new backup destination: its name, its passphrase, and exactly the settings and the secrets of its kind.
/// </summary>
public sealed class CreateBackupDestinationRequestValidator : AbstractValidator<CreateBackupDestinationRequest>
{
    public const int MinimumPassphraseLength = 12;

    public CreateBackupDestinationRequestValidator()
    {
        RuleFor(r => r.Name).MustBeDestinationName();
        RuleFor(r => r.Kind).IsInEnum();
        RuleFor(r => r.Passphrase)
            .NotEmpty()
            .MinimumLength(MinimumPassphraseLength)
            .WithMessage($"The passphrase needs at least {MinimumPassphraseLength} characters.");

        RuleFor(r => r.Local).NotNull().When(r => r.Kind == BackupDestinationKind.Local)
            .WithMessage("A local destination needs its settings.");
        RuleFor(r => r.S3).NotNull().When(r => r.Kind is BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible)
            .WithMessage("An S3 destination needs its settings.");
        RuleFor(r => r.Gcs).NotNull().When(r => r.Kind == BackupDestinationKind.GoogleCloudStorage)
            .WithMessage("A Google Cloud Storage destination needs its settings.");
        RuleFor(r => r.Azure).NotNull().When(r => r.Kind == BackupDestinationKind.AzureBlob)
            .WithMessage("An Azure Blob Storage destination needs its settings.");
        RuleFor(r => r.Ftp).NotNull().When(r => r.Kind == BackupDestinationKind.Ftp)
            .WithMessage("An FTP destination needs its settings.");
        RuleFor(r => r)
            .Must(r => BackupDestinationRules.CountSettings(r.Local, r.S3, r.Gcs, r.Azure, r.Ftp) <= 1)
            .OverridePropertyName("settings")
            .WithMessage("Set only the settings of the kind of the destination.");

        BackupDestinationRules.AddSettingsRules(
            this,
            r => r.Local, 
            r => r.S3, 
            r => r.Gcs, 
            r => r.Azure,
            r => r.Ftp);

        RuleFor(r => r.Endpoint())
            .NotEmpty()
            .When(r => r.Kind == BackupDestinationKind.S3Compatible && r.S3 is not null)
            .OverridePropertyName("s3.endpoint")
            .WithMessage("An S3-compatible destination needs the URL of its API.");
        RuleFor(r => r.Credentials!.SecretAccessKey)
            .NotEmpty()
            .When(r => r.Kind is BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible)
            .OverridePropertyName("credentials.secretAccessKey")
            .WithMessage("An S3 destination needs the secret of its access key.");
        RuleFor(r => r.Credentials!.ServiceAccountJson)
            .NotEmpty()
            .When(r => r.Kind == BackupDestinationKind.GoogleCloudStorage)
            .OverridePropertyName("credentials.serviceAccountJson")
            .WithMessage("A Google Cloud Storage destination needs the key of a service account.");
        RuleFor(r => r.Credentials)
            .Must(c => !string.IsNullOrEmpty(c?.AccountKey) || !string.IsNullOrEmpty(c?.SasToken))
            .When(r => r.Kind == BackupDestinationKind.AzureBlob)
            .OverridePropertyName("credentials")
            .WithMessage("An Azure Blob Storage destination needs the key of its account or a shared access signature.");
        RuleFor(r => r.Credentials!.Password)
            .NotEmpty()
            .When(r => r.Kind == BackupDestinationKind.Ftp)
            .OverridePropertyName("credentials.password")
            .WithMessage("An FTP destination needs the password of its user.");
    }
}

/// <summary>
/// Checks a change of a backup destination: its name, and the settings given. That they are the ones of its kind is
/// checked once the destination is read.
/// </summary>
public sealed class UpdateBackupDestinationRequestValidator : AbstractValidator<UpdateBackupDestinationRequest>
{
    public UpdateBackupDestinationRequestValidator()
    {
        RuleFor(r => r.Name).MustBeDestinationName();
        RuleFor(r => r)
            .Must(r => BackupDestinationRules.CountSettings(r.Local, r.S3, r.Gcs, r.Azure, r.Ftp) == 1)
            .OverridePropertyName("settings")
            .WithMessage("Set the settings of the kind of the destination, and only them.");
        BackupDestinationRules.AddSettingsRules(this, r => r.Local, r => r.S3, r => r.Gcs, r => r.Azure, r => r.Ftp);
    }
}

/// <summary>
/// Checks the destinations of an instance: no identifier twice.
/// </summary>
public sealed class InstanceBackupDestinationsValidator : AbstractValidator<InstanceBackupDestinations>
{
    public InstanceBackupDestinationsValidator()
    {
        RuleFor(r => r.DestinationIds)
            .NotNull()
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("A destination is listed twice.");
    }
}

/// <summary>
/// Checks a request for a backup.
/// </summary>
public sealed class StartBackupRequestValidator : AbstractValidator<StartBackupRequest>
{
    public StartBackupRequestValidator()
    {
        RuleFor(r => r.Kind).IsInEnum();
        RuleFor(r => r.DestinationIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("A destination is listed twice.");
    }
}

/// <summary>
/// Checks a request for the verification of a backup.
/// </summary>
public sealed class VerifyBackupRequestValidator : AbstractValidator<VerifyBackupRequest>
{
    public VerifyBackupRequestValidator()
    {
        RuleFor(r => r.DestinationId).NotEmpty();
    }
}

internal static class BackupDestinationRules
{
    public static IRuleBuilderOptions<T, string> MustBeDestinationName<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .NotEmpty()
            .MaximumLength(64)
            .Matches(@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")
            .WithMessage("The name starts with a letter or a digit, then holds letters, digits, spaces, dots, dashes and underscores.");
    }

    public static int CountSettings(params object?[] settings)
    {
        return settings.Count(s => s is not null);
    }

    public static string? Endpoint(this CreateBackupDestinationRequest request)
    {
        return request.S3?.Endpoint;
    }

    public static void AddSettingsRules<T>(
        AbstractValidator<T> validator,
        Func<T, LocalDestinationSettings?> local,
        Func<T, S3DestinationSettings?> s3,
        Func<T, GcsDestinationSettings?> gcs,
        Func<T, AzureBlobDestinationSettings?> azure,
        Func<T, FtpDestinationSettings?> ftp)
    {
        validator.RuleFor(r => local(r)!.Path)
            .Must(Path.IsPathFullyQualified)
            .When(r => local(r) is not null)
            .OverridePropertyName("local.path")
            .WithMessage("The folder must be an absolute path.");

        validator.RuleFor(r => s3(r)!.Bucket).NotEmpty().When(r => s3(r) is not null).OverridePropertyName("s3.bucket");
        validator.RuleFor(r => s3(r)!.Region).NotEmpty().When(r => s3(r) is not null).OverridePropertyName("s3.region");
        validator.RuleFor(r => s3(r)!.AccessKeyId).NotEmpty().When(r => s3(r) is not null).OverridePropertyName("s3.accessKeyId");
        validator.RuleFor(r => s3(r)!.Endpoint)
            .Must(BeHttpUrl!)
            .When(r => !string.IsNullOrEmpty(s3(r)?.Endpoint))
            .OverridePropertyName("s3.endpoint")
            .WithMessage("The endpoint must be an http or https URL.");
        validator.RuleFor(r => s3(r)!.Prefix).Must(BeValidPrefix).When(r => s3(r) is not null).OverridePropertyName("s3.prefix")
            .WithMessage(PrefixMessage);

        validator.RuleFor(r => gcs(r)!.Bucket).NotEmpty().When(r => gcs(r) is not null).OverridePropertyName("gcs.bucket");
        validator.RuleFor(r => gcs(r)!.Prefix).Must(BeValidPrefix).When(r => gcs(r) is not null).OverridePropertyName("gcs.prefix")
            .WithMessage(PrefixMessage);

        validator.RuleFor(r => azure(r)!.AccountUrl)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            .When(r => azure(r) is not null && !IsLoopbackHttp(azure(r)!.AccountUrl))
            .OverridePropertyName("azure.accountUrl")
            .WithMessage("The URL of the account must be an https URL.");
        validator.RuleFor(r => azure(r)!.Container).NotEmpty().When(r => azure(r) is not null).OverridePropertyName("azure.container");
        validator.RuleFor(r => azure(r)!.Prefix).Must(BeValidPrefix).When(r => azure(r) is not null).OverridePropertyName("azure.prefix")
            .WithMessage(PrefixMessage);

        validator.RuleFor(r => ftp(r)!.Host).NotEmpty().When(r => ftp(r) is not null).OverridePropertyName("ftp.host");
        validator.RuleFor(r => ftp(r)!.Port).InclusiveBetween(1, 65535).When(r => ftp(r)?.Port is not null).OverridePropertyName("ftp.port");
        validator.RuleFor(r => ftp(r)!.Encryption).IsInEnum().When(r => ftp(r) is not null).OverridePropertyName("ftp.encryption");
        validator.RuleFor(r => ftp(r)!.Path).NotEmpty().When(r => ftp(r) is not null).OverridePropertyName("ftp.path");
        validator.RuleFor(r => ftp(r)!.UserName).NotEmpty().When(r => ftp(r) is not null).OverridePropertyName("ftp.userName");
    }

    private const string PrefixMessage =
        "The prefix holds folders of letters, digits, dots, dashes and underscores, separated by slashes.";

    private static bool BeValidPrefix(string? prefix)
    {
        return string.IsNullOrEmpty(prefix) || P4ServerManager.Backup.Targets.BackupObjectKey.IsValid(prefix.Trim('/'));
    }

    private static bool BeHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>
    /// An emulator on the machine, such as Azurite, listens in plain http.
    /// </summary>
    private static bool IsLoopbackHttp(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
