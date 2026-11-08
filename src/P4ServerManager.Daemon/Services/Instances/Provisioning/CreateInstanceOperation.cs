using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates a new server: a new database, its SSL certificate when the instance uses SSL, its super user and the
/// recommended configuration.
/// </summary>
public sealed class CreateInstanceOperation(
    ResolveBuildStep resolveBuild,
    PrepareDirectoriesStep prepareDirectories,
    InitializeDatabaseStep initializeDatabase,
    GenerateSslCertificateStep generateSslCertificate,
    StartServerStep startServer,
    CreateSuperUserStep createSuperUser,
    ConfigureServerStep configureServer,
    CreateSpecsStep createSpecs) : IOperationDefinition<CreateInstanceContext>
{
    public const string OperationKind = "provision";

    public string Kind => OperationKind;

    public OperationPlan<CreateInstanceContext> Plan(CreateInstanceContext context)
    {
        return new OperationPlan<CreateInstanceContext>()
            .Then(resolveBuild)
            .Then(prepareDirectories)
            .Then(initializeDatabase)
            .ThenIf(context.Instance.Ssl, generateSslCertificate)
            .Then(startServer)
            .Then(createSuperUser)
            .Then(configureServer)
            .Then(createSpecs);
    }
}
