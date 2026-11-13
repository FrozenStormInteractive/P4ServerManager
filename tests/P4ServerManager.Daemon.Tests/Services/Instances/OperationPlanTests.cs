using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Instances.UnicodeConversion;
using P4ServerManager.Daemon.Services.Instances.Upgrade;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

/// <summary>
/// The plans of the operations on an instance. Building a plan runs no step: the steps get no services.
/// </summary>
public sealed class OperationPlanTests
{
    private static readonly P4ServerBuild Current = CreateBuild("2025.1", "2780000");
    private static readonly P4ServerBuild Newer = CreateBuild("2025.1", "2790000");

    private readonly ResolveBuildStep _resolveBuild = new(null!, null!);
    private readonly StopServerStep _stopServer = new(null!);
    private readonly StartServerStep _startServer = new(null!, null!, null!);
    private readonly UpgradeDatabaseStep _upgradeDatabase = new(null!, null!, null!);
    private readonly SaveCheckpointStep _saveCheckpoint = new(null!, null!);

    [Theory]
    [InlineData(true, new[] { "prepare-directories", "initialize-database", "generate-ssl-certificate" })]
    [InlineData(false, new[] { "prepare-directories", "initialize-database" })]
    public void CreateInstance_GeneratesACertificateOnlyWithSsl(bool ssl, string[] firstSteps)
    {
        CreateInstanceOperation operation = new(
            _resolveBuild,
            new PrepareDirectoriesStep(null!),
            new InitializeDatabaseStep(null!, null!),
            new GenerateSslCertificateStep(null!, null!, null!),
            _startServer,
            new CreateSuperUserStep(null!, null!, null!, null!),
            new ConfigureServerStep(null!, null!, null!, null!),
            new CreateSpecsStep(null!, null!, null!, null!));
        P4ServerInstance instance = CreateInstance(ssl);

        OperationPlan<CreateInstanceContext> plan = operation.Plan(
            new CreateInstanceContext(null!, instance, new SuperUserCredentials("admin", "secret")));

        plan.Steps.Select(s => s.Step.Key).ShouldBe(
            ["resolve-build", .. firstSteps, "start-server", "create-super-user", "configure-server", "create-specs"]);
    }

    [Fact]
    public void ImportInstance_ReadsTheFingerprintOnlyWithSsl()
    {
        ImportInstanceOperation operation = new(
            _resolveBuild,
            new CheckRootStep(null!),
            new ReadSslFingerprintStep(null!, null!, null!),
            _saveCheckpoint,
            new ReadServerSettingsStep(null!, null!),
            _upgradeDatabase,
            _startServer);

        operation.Plan(new ImportInstanceContext(null!, CreateInstance(ssl: false), "checkpoint.gz"))
            .Steps.Select(s => s.Step.Key)
            .ShouldNotContain("read-ssl-fingerprint");
    }

    [Theory]
    [InlineData(true, false, new[] { false, false, false, false })]
    [InlineData(false, false, new[] { false, true, false, true })]
    [InlineData(true, true, new[] { false, true, true, true })]
    public void Upgrade_SkipsWhatTheInstanceDoesNotNeed(bool wasRunning, bool upToDate, bool[] skipped)
    {
        UpgradeOperation operation = new(_resolveBuild, _stopServer, _upgradeDatabase, _startServer);
        P4ServerRelease.TryParse("r25.1", out P4ServerRelease release);
        UpgradeContext context = new(null!, CreateInstance(ssl: false, Current), release, wasRunning);
        context.Values.Set(ResolveBuildStep.ResolvedBuild, upToDate ? Current : Newer);

        OperationPlan<UpgradeContext> plan = operation.Plan(context);

        plan.Steps.Select(s => s.Step.Key).ShouldBe(
            ["resolve-build", "stop-server", "upgrade-database", "start-server"]);
        plan.Steps.Select(s => s.SkipWhen?.Invoke(context) == true).ShouldBe(skipped);
        context.Outcome.ShouldBe(upToDate ? UpgradeContext.UpToDateOutcome : UpgradeContext.UpgradedOutcome);
    }

    [Theory]
    [InlineData(true, new[] { false, false, false, false })]
    [InlineData(false, new[] { true, false, false, true })]
    public void UnicodeConversion_LeavesAStoppedServerStopped(bool wasRunning, bool[] skipped)
    {
        UnicodeConversionOperation operation = new(
            _stopServer,
            _saveCheckpoint,
            new SwitchToUnicodeStep(null!, null!, null!, null!),
            _startServer);
        UnicodeConversionContext context = new(
            null!,
            CreateInstance(ssl: false, Current),
            wasRunning,
            "checkpoint.gz");

        OperationPlan<UnicodeConversionContext> plan = operation.Plan(context);

        plan.Steps.Select(s => s.Step.Key).ShouldBe(
            ["stop-server", "save-checkpoint", "switch-to-unicode", "start-server"]);
        plan.Steps.Select(s => s.SkipWhen?.Invoke(context) == true).ShouldBe(skipped);
    }

    [Theory]
    [InlineData(true, new[] { "stop-server", "set-server-id", "start-server", "rename-server-spec" })]
    [InlineData(false, new[] { "set-server-id", "start-server", "rename-server-spec", "stop-server" })]
    public void ServerIdAssignment_StartsAStoppedServerOnlyToRenameItsSpec(bool wasRunning, string[] keys)
    {
        ServerIdAssignmentOperation operation = new(
            _stopServer,
            new SetServerIdStep(null!, null!, null!),
            _startServer,
            new RenameServerSpecStep(null!, null!, null!, null!));
        ServerIdAssignmentContext context = new(
            null!,
            CreateInstance(ssl: false, Current),
            wasRunning,
            "edge",
            new SuperUserCredentials("admin", "secret"));

        OperationPlan<ServerIdAssignmentContext> plan = operation.Plan(context);

        plan.Steps.Select(s => s.Step.Key).ShouldBe(keys);
        plan.Steps.ShouldAllBe(s => s.SkipWhen == null);
    }

    private static P4ServerInstance CreateInstance(bool ssl, P4ServerBuild? build = null)
    {
        return new P4ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "main",
            RootPath = "/srv/main/root",
            Release = "r25.1",
            Ssl = ssl,
            Build = build,
            BuildId = build?.Id,
        };
    }

    private static P4ServerBuild CreateBuild(string version, string buildNumber)
    {
        return new P4ServerBuild
        {
            Id = Guid.NewGuid(),
            Release = "r25.1",
            Version = version,
            BuildNumber = buildNumber,
            Platform = "linux26x86_64",
            Sha256 = string.Empty,
        };
    }
}
