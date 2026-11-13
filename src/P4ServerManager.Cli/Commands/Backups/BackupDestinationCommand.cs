using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Backups;

/// <summary>
/// The <c>backup-destination</c> command and its subcommands: the places the instances back up to.
/// </summary>
internal static class BackupDestinationCommand
{
    /// <summary>
    /// Variable read for the passphrase of a new repository when no terminal can ask for it.
    /// </summary>
    public const string PassphraseVariable = "P4SM_BACKUP_PASSPHRASE";

    /// <summary>
    /// Variable read for the secret of the storage (S3 secret key, Azure key or SAS, FTP password) when no terminal can
    /// ask for it.
    /// </summary>
    public const string SecretVariable = "P4SM_BACKUP_SECRET";

    private static readonly Dictionary<string, BackupDestinationKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = BackupDestinationKind.Local,
        ["s3"] = BackupDestinationKind.S3,
        ["s3-compatible"] = BackupDestinationKind.S3Compatible,
        ["gcs"] = BackupDestinationKind.GoogleCloudStorage,
        ["azure"] = BackupDestinationKind.AzureBlob,
        ["ftp"] = BackupDestinationKind.Ftp,
    };

    public static Command Create(CliServices services)
    {
        Command command = new(
            "backup-destination",
            "Manage the places the instances back up to: local folders, S3 or S3-compatible buckets, Google Cloud "
            + "Storage buckets, Azure Blob Storage containers and FTP folders.");
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateShow(services));
        command.Subcommands.Add(CreateAdd(services));
        command.Subcommands.Add(CreateSet(services));
        command.Subcommands.Add(CreateTest(services));
        command.Subcommands.Add(CreatePoints(services));
        command.Subcommands.Add(CreateRemove(services));
        return command;
    }

    private static Command CreatePoints(CliServices services)
    {
        Argument<string> destinationArgument = CreateDestinationArgument();
        Option<string?> instanceOption = new("--instance")
        {
            Description = "Only the points of this instance: the name it had when it was backed up, or its identifier.",
            HelpName = "name",
        };
        Command command = new(
            "points",
            "List the backup points stored at a destination, newest first. They are read from the destination itself: "
            + "the points of instances this daemon never knew are listed too.")
        {
            destinationArgument,
            instanceOption,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationArgument),
                cancellationToken);
            IReadOnlyList<BackupPointDto> points = await client.GetBackupPointsAsync(destination.Id, null, cancellationToken);
            if (invocation.GetValue(instanceOption) is { } source)
            {
                points = points
                    .Where(p => string.Equals(p.SourceName, source, StringComparison.OrdinalIgnoreCase)
                        || p.SourceId.ToString().Equals(source, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (invocation.Json)
            {
                invocation.WriteJson(points, CliJsonContext.Default.IReadOnlyListBackupPointDto);
                return 0;
            }
            if (points.Count == 0)
            {
                await invocation.Output.WriteLineAsync($"{destination.Name} holds no backup point.");
                return 0;
            }
            BackupPointFormatting.WriteTable(invocation.Output, points);
            return 0;
        });
        return command;
    }

    private static Command CreateList(CliServices services)
    {
        Command command = new("list", "List the backup destinations.");
        command.Aliases.Add("ls");
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            IReadOnlyList<BackupDestinationDto> destinations = await client.GetBackupDestinationsAsync(cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(destinations, CliJsonContext.Default.IReadOnlyListBackupDestinationDto);
                return 0;
            }
            if (destinations.Count == 0)
            {
                await invocation.Output.WriteLineAsync(
                    "No backup destinations. Add one with 'p4sm backup-destination add'.");
                return 0;
            }

            TableWriter table = new("NAME", "KIND", "LOCATION", "INSTANCES");
            foreach (BackupDestinationDto destination in destinations)
            {
                table.AddRow(
                    destination.Name,
                    FormatKind(destination.Kind),
                    destination.Location,
                    destination.InstanceCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    private static Command CreateShow(CliServices services)
    {
        Argument<string> destinationArgument = CreateDestinationArgument();
        Command command = new("show", "Show a backup destination.") { destinationArgument };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationArgument),
                cancellationToken);
            await WriteDestinationAsync(invocation, destination);
            return 0;
        });
        return command;
    }

    private static Command CreateAdd(CliServices services)
    {
        Argument<string> nameArgument = new("name") { Description = "Unique name of the destination." };
        Option<string> kindOption = new("--kind")
        {
            Description = "Kind of storage: local, s3, s3-compatible, gcs, azure or ftp.",
            Required = true,
        };
        kindOption.AcceptOnlyFromAmong([.. Kinds.Keys]);
        SettingsOptions settings = new();
        Option<bool> stdinOption = new("--secrets-stdin")
        {
            Description = "Read the passphrase, then the secret of the storage if it needs one, from the standard "
                + $"input, one per line. Otherwise they are asked on the terminal, or read from {PassphraseVariable} "
                + $"and {SecretVariable}.",
        };
        Command command = new(
            "add",
            "Add a backup destination. A storage that holds no repository gets a new one, encrypted with a passphrase; "
            + "a storage that already holds one is attached when the passphrase opens it. Keep the passphrase "
            + "somewhere else: it is the only way to read the backups from another daemon.")
        {
            nameArgument,
            kindOption,
            stdinOption,
        };
        settings.AddTo(command);

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            BackupDestinationKind kind = Kinds[invocation.GetValue(kindOption)!];
            SecretReader secrets = new(invocation, invocation.GetValue(stdinOption));
            string passphrase = await secrets.ReadAsync(
                PassphraseVariable,
                "Passphrase of the repository: ",
                confirm: true,
                cancellationToken);
            BackupDestinationCredentials credentials = await settings.ReadCredentialsAsync(
                invocation,
                kind,
                secrets,
                cancellationToken);

            CreateBackupDestinationRequest request = settings.Apply(new CreateBackupDestinationRequest
            {
                Name = invocation.GetValue(nameArgument)!,
                Kind = kind,
                Credentials = credentials,
                Passphrase = passphrase,
            }, invocation, kind);

            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.CreateBackupDestinationAsync(request, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(destination, CliJsonContext.Default.BackupDestinationDto);
                return 0;
            }
            await invocation.Output.WriteLineAsync(
                $"Added backup destination {destination.Name} at {destination.Location}. Keep its passphrase "
                + "somewhere else: it is the only way to read the backups from another daemon.");
            return 0;
        });
        return command;
    }

    private static Command CreateSet(CliServices services)
    {
        Argument<string> destinationArgument = CreateDestinationArgument();
        Option<string?> nameOption = new("--name") { Description = "New name of the destination." };
        Option<bool> secretOption = new("--change-secret")
        {
            Description = "Ask for a new secret of the storage. Otherwise the stored one is kept.",
        };
        Option<bool> stdinOption = new("--secrets-stdin")
        {
            Description = "Read the new secret of the storage from the standard input.",
        };
        SettingsOptions settings = new();
        Command command = new(
            "set",
            "Change a backup destination. Its kind and its passphrase stay; the settings not given keep their value.")
        {
            destinationArgument,
            nameOption,
            secretOption,
            stdinOption,
        };
        settings.AddTo(command);

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto current = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationArgument),
                cancellationToken);
            BackupDestinationCredentials? credentials = null;
            if (invocation.GetValue(secretOption) || invocation.GetValue(stdinOption))
            {
                SecretReader secrets = new(invocation, invocation.GetValue(stdinOption));
                credentials = await settings.ReadCredentialsAsync(invocation, current.Kind, secrets, cancellationToken);
            }

            UpdateBackupDestinationRequest request = settings.Apply(
                new UpdateBackupDestinationRequest
                {
                    Name = invocation.GetValue(nameOption) ?? current.Name,
                    Local = current.Local,
                    S3 = current.S3,
                    Gcs = current.Gcs,
                    Azure = current.Azure,
                    Ftp = current.Ftp,
                    Credentials = credentials,
                },
                invocation,
                current.Kind);
            BackupDestinationDto destination = await client.UpdateBackupDestinationAsync(current.Id, request, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(destination, CliJsonContext.Default.BackupDestinationDto);
                return 0;
            }
            await invocation.Output.WriteLineAsync($"Changed backup destination {destination.Name}.");
            return 0;
        });
        return command;
    }

    private static Command CreateTest(CliServices services)
    {
        Argument<string> destinationArgument = CreateDestinationArgument();
        Command command = new(
            "test",
            "Test a backup destination: write, read back and delete a small object. Exits with an error when it fails.")
        {
            destinationArgument,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationArgument),
                cancellationToken);
            BackupDestinationTestResult result = await client.TestBackupDestinationAsync(destination.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(result, CliJsonContext.Default.BackupDestinationTestResult);
            }
            else if (result.Succeeded)
            {
                string capabilities = result.Capabilities.Count == 0 ? "none" : string.Join(", ", result.Capabilities);
                await invocation.Output.WriteLineAsync(
                    $"{destination.Name} works. Guarantees of the storage: {capabilities}.");
            }
            else
            {
                await invocation.Error.WriteLineAsync($"Error: {result.Error}");
            }
            return result.Succeeded ? 0 : CliServices.ErrorExitCode;
        });
        return command;
    }

    private static Command CreateRemove(CliServices services)
    {
        Argument<string> destinationArgument = CreateDestinationArgument();
        Option<bool> yesOption = new("--yes", "-y") { Description = "Remove without asking for a confirmation." };
        Command command = new(
            "remove",
            "Remove a backup destination from the daemon. Its repository and its backups stay on the storage.")
        {
            destinationArgument,
            yesOption,
        };
        command.Aliases.Add("rm");
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationArgument),
                cancellationToken);
            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException("Removing a destination asks for a confirmation. Without a terminal, pass --yes.");
                }
                if (!prompter.Confirm($"Remove backup destination {destination.Name}? Its backups stay on the storage."))
                {
                    await invocation.Error.WriteLineAsync("Nothing was removed.");
                    return CliServices.ErrorExitCode;
                }
            }

            await client.DeleteBackupDestinationAsync(destination.Id, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Removed backup destination {destination.Name}.");
            }
            return 0;
        });
        return command;
    }

    private static async Task WriteDestinationAsync(CliInvocation invocation, BackupDestinationDto destination)
    {
        if (invocation.Json)
        {
            invocation.WriteJson(destination, CliJsonContext.Default.BackupDestinationDto);
            return;
        }

        List<(string Label, string? Value)> fields =
        [
            ("Id", destination.Id.ToString()),
            ("Name", destination.Name),
            ("Kind", FormatKind(destination.Kind)),
            ("Location", destination.Location),
            ("Region", destination.S3?.Region),
            ("Endpoint", destination.S3?.Endpoint),
            ("Access key", destination.S3?.AccessKeyId),
            ("Storage class", destination.S3?.StorageClass),
            ("Encryption", destination.Ftp?.Encryption.ToString()),
            ("User", destination.Ftp?.UserName),
            ("Certificate", destination.Ftp?.CertificateFingerprint),
            ("Credentials", destination.HasCredentials ? "stored" : "none"),
            ("Repository", destination.RepositoryId.ToString()),
            ("Instances", destination.InstanceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];
        int width = fields.Max(f => f.Label.Length) + 2;
        foreach ((string label, string? value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            await invocation.Output.WriteLineAsync($"{(label + ":").PadRight(width)}{value}");
        }
    }

    private static Argument<string> CreateDestinationArgument()
    {
        return new Argument<string>("destination") { Description = "Name or identifier of the backup destination." };
    }

    public static string FormatKind(BackupDestinationKind kind)
    {
        return Kinds.First(k => k.Value == kind).Key;
    }

    /// <summary>
    /// Reads secrets from the standard input, the environment or the terminal, in this order.
    /// </summary>
    private sealed class SecretReader(CliInvocation invocation, bool fromStandardInput)
    {
        public async Task<string> ReadAsync(
            string variable,
            string prompt,
            bool confirm,
            CancellationToken cancellationToken)
        {
            if (fromStandardInput)
            {
                string? line = await invocation.Environment.Input.ReadLineAsync(cancellationToken);
                return line is { Length: > 0 }
                    ? line
                    : throw new CliException($"No value was read from the standard input for '{prompt.TrimEnd(' ', ':')}'.");
            }
            if (invocation.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                return value;
            }

            IPrompter prompter = invocation.Environment.Prompter;
            if (!prompter.IsInteractive)
            {
                throw new CliException(
                    $"'{prompt.TrimEnd(' ', ':')}' is asked on a terminal. Without one, set {variable} or pass --secrets-stdin.");
            }
            string secret = prompter.ReadSecret(prompt);
            if (secret.Length == 0)
            {
                throw new CliException($"'{prompt.TrimEnd(' ', ':')}' cannot be empty.");
            }
            if (confirm && prompter.ReadSecret("Confirm: ") != secret)
            {
                throw new CliException("The values do not match.");
            }
            return secret;
        }
    }

    /// <summary>
    /// Options of the settings of every kind of destination. Each kind reads the options it knows.
    /// </summary>
    private sealed class SettingsOptions
    {
        private readonly Option<string?> path = new("--path") { Description = "Local and FTP: the folder." };
        private readonly Option<string?> bucket = new("--bucket") { Description = "S3 and GCS: the bucket." };
        private readonly Option<string?> prefix = new("--prefix")
        {
            Description = "S3, GCS and Azure: folder of the repository in the bucket or the container.",
        };
        private readonly Option<string?> region = new("--region") { Description = "S3: region of the bucket." };
        private readonly Option<string?> endpoint = new("--endpoint")
        {
            Description = "S3-compatible: URL of the API.",
        };
        private readonly Option<bool?> pathStyle = new("--path-style")
        {
            Description = "S3-compatible: put the bucket in the path of the URLs.",
        };
        private readonly Option<string?> accessKeyId = new("--access-key-id") { Description = "S3: identifier of the access key." };
        private readonly Option<string?> storageClass = new("--storage-class") { Description = "S3: storage class of the objects." };
        private readonly Option<string?> accountUrl = new("--account-url")
        {
            Description = "Azure: URL of the Blob service, such as https://account.blob.core.windows.net.",
        };
        private readonly Option<string?> container = new("--container") { Description = "Azure: the container." };
        private readonly Option<bool> sas = new("--sas")
        {
            Description = "Azure: the secret is a shared access signature of the container, not the key of the account.",
        };
        private readonly Option<string?> serviceAccountFile = new("--service-account-file")
        {
            Description = "GCS: JSON key file of a service account.",
        };
        private readonly Option<string?> host = new("--host") { Description = "FTP: the server." };
        private readonly Option<int?> port = new("--port") { Description = "FTP: the port, 21 or 990 by default." };
        private readonly Option<string?> encryption = new("--encryption")
        {
            Description = "FTP: explicit (default), implicit or none.",
        };
        private readonly Option<string?> user = new("--user") { Description = "FTP: the user." };
        private readonly Option<string?> fingerprint = new("--certificate-fingerprint")
        {
            Description = "FTP: SHA-256 fingerprint of a self-signed certificate to trust.",
        };

        public SettingsOptions()
        {
            encryption.AcceptOnlyFromAmong("explicit", "implicit", "none");
        }

        public void AddTo(Command command)
        {
            foreach (Option option in new Option[]
            {
                path, bucket, prefix, region, endpoint, pathStyle, accessKeyId, storageClass, accountUrl, container, sas,
                serviceAccountFile, host, port, encryption, user, fingerprint,
            })
            {
                command.Options.Add(option);
            }
        }

        public CreateBackupDestinationRequest Apply(
            CreateBackupDestinationRequest request,
            CliInvocation invocation,
            BackupDestinationKind kind)
        {
            (LocalDestinationSettings? local, S3DestinationSettings? s3, GcsDestinationSettings? gcs,
                AzureBlobDestinationSettings? azure, FtpDestinationSettings? ftp) = Build(invocation, kind, request.Name, null);
            return request with { Local = local, S3 = s3, Gcs = gcs, Azure = azure, Ftp = ftp };
        }

        public UpdateBackupDestinationRequest Apply(
            UpdateBackupDestinationRequest request,
            CliInvocation invocation,
            BackupDestinationKind kind)
        {
            (LocalDestinationSettings? local, S3DestinationSettings? s3, GcsDestinationSettings? gcs,
                AzureBlobDestinationSettings? azure, FtpDestinationSettings? ftp) = Build(invocation, kind, request.Name, request);
            return request with { Local = local, S3 = s3, Gcs = gcs, Azure = azure, Ftp = ftp };
        }

        public async Task<BackupDestinationCredentials> ReadCredentialsAsync(
            CliInvocation invocation,
            BackupDestinationKind kind,
            SecretReader secrets,
            CancellationToken cancellationToken)
        {
            switch (kind)
            {
                case BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible:
                    return new BackupDestinationCredentials
                    {
                        SecretAccessKey = await secrets.ReadAsync(SecretVariable, "Secret access key: ", false, cancellationToken),
                    };
                case BackupDestinationKind.AzureBlob:
                    string azureSecret = await secrets.ReadAsync(
                        SecretVariable,
                        invocation.GetValue(sas) ? "Shared access signature: " : "Account key: ",
                        false,
                        cancellationToken);
                    return invocation.GetValue(sas)
                        ? new BackupDestinationCredentials { SasToken = azureSecret }
                        : new BackupDestinationCredentials { AccountKey = azureSecret };
                case BackupDestinationKind.Ftp:
                    return new BackupDestinationCredentials
                    {
                        Password = await secrets.ReadAsync(SecretVariable, "FTP password: ", false, cancellationToken),
                    };
                case BackupDestinationKind.GoogleCloudStorage:
                    string file = invocation.GetValue(serviceAccountFile)
                        ?? throw new CliException("A Google Cloud Storage destination needs --service-account-file.");
                    return new BackupDestinationCredentials
                    {
                        ServiceAccountJson = await File.ReadAllTextAsync(file, cancellationToken),
                    };
                default:
                    return new BackupDestinationCredentials();
            }
        }

        private (LocalDestinationSettings?, S3DestinationSettings?, GcsDestinationSettings?, AzureBlobDestinationSettings?,
            FtpDestinationSettings?) Build(
                CliInvocation invocation,
                BackupDestinationKind kind,
                string name,
                UpdateBackupDestinationRequest? current)
        {
            string Required(Option<string?> option, string? fallback)
            {
                return invocation.GetValue(option) ?? fallback
                    ?? throw new CliException($"A destination of kind {FormatKind(kind)} needs {option.Name}.");
            }

            switch (kind)
            {
                case BackupDestinationKind.Local:
                    return (new LocalDestinationSettings { Path = Path.GetFullPath(Required(path, current?.Local?.Path)) },
                        null, null, null, null);
                case BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible:
                    S3DestinationSettings? s3 = current?.S3;
                    return (null, new S3DestinationSettings
                    {
                        Bucket = Required(bucket, s3?.Bucket),
                        Prefix = invocation.GetValue(prefix) ?? s3?.Prefix,
                        Region = invocation.GetValue(region) ?? s3?.Region
                            ?? (kind == BackupDestinationKind.S3Compatible ? "us-east-1" : Required(region, null)),
                        Endpoint = kind == BackupDestinationKind.S3Compatible ? Required(endpoint, s3?.Endpoint) : null,
                        ForcePathStyle = invocation.GetValue(pathStyle) ?? s3?.ForcePathStyle ?? kind == BackupDestinationKind.S3Compatible,
                        AccessKeyId = Required(accessKeyId, s3?.AccessKeyId),
                        StorageClass = invocation.GetValue(storageClass) ?? s3?.StorageClass,
                    }, null, null, null);
                case BackupDestinationKind.GoogleCloudStorage:
                    return (null, null, new GcsDestinationSettings
                    {
                        Bucket = Required(bucket, current?.Gcs?.Bucket),
                        Prefix = invocation.GetValue(prefix) ?? current?.Gcs?.Prefix,
                    }, null, null);
                case BackupDestinationKind.AzureBlob:
                    return (null, null, null, new AzureBlobDestinationSettings
                    {
                        AccountUrl = Required(accountUrl, current?.Azure?.AccountUrl),
                        Container = Required(container, current?.Azure?.Container),
                        Prefix = invocation.GetValue(prefix) ?? current?.Azure?.Prefix,
                    }, null);
                case BackupDestinationKind.Ftp:
                    FtpDestinationSettings? ftp = current?.Ftp;
                    return (null, null, null, null, new FtpDestinationSettings
                    {
                        Host = Required(host, ftp?.Host),
                        Port = invocation.GetValue(port) ?? ftp?.Port,
                        Encryption = invocation.GetValue(encryption) switch
                        {
                            "implicit" => FtpEncryption.Implicit,
                            "none" => FtpEncryption.None,
                            "explicit" => FtpEncryption.Explicit,
                            _ => ftp?.Encryption ?? FtpEncryption.Explicit,
                        },
                        Path = Required(path, ftp?.Path),
                        UserName = Required(user, ftp?.UserName),
                        CertificateFingerprint = invocation.GetValue(fingerprint) ?? ftp?.CertificateFingerprint,
                    });
                default:
                    throw new CliException($"Destinations of kind {kind} are not supported by this CLI. Name: {name}.");
            }
        }
    }
}
