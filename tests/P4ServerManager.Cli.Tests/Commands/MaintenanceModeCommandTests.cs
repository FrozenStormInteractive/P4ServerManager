using System.Net;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class MaintenanceModeCommandTests
{
    private readonly CliRunner _cli = new();

    [Fact]
    public async Task Enable_RestartsARunningInstanceInMaintenanceMode()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstance(main, "enable", main with { MaintenanceMode = true });

        CliResult result = await _cli.RunAsync("instance", "maintenance", "enable", "main");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, $"/api/instances/{main.Id}/maintenance/enable").ShouldHaveSingleItem();
        result.OutputLines.ShouldBe(["Restarting main in maintenance mode...", "main runs in maintenance mode."]);
    }

    [Fact]
    public async Task Enable_StartsAStoppedInstanceInMaintenanceMode()
    {
        InstanceDto main = CliRunner.Instance("main", runtimeStatus: InstanceRuntimeStatus.Stopped);
        SetInstance(main, "enable", main with { MaintenanceMode = true, RuntimeStatus = InstanceRuntimeStatus.Running });

        CliResult result = await _cli.RunAsync("instance", "maintenance", "enable", "main");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(["Starting main in maintenance mode...", "main runs in maintenance mode."]);
    }

    [Fact]
    public async Task Disable_RestartsARunningInstanceInNormalMode()
    {
        InstanceDto main = CliRunner.Instance("main", maintenanceMode: true);
        SetInstance(main, "disable", main with { MaintenanceMode = false });

        CliResult result = await _cli.RunAsync("instance", "maintenance", "disable", "main");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, $"/api/instances/{main.Id}/maintenance/disable").ShouldHaveSingleItem();
        result.OutputLines.ShouldBe(["Restarting main in normal mode...", "main runs in normal mode."]);
    }

    [Fact]
    public async Task Disable_LeavesAStoppedInstanceStopped()
    {
        InstanceDto main = CliRunner.Instance(
            "main",
            runtimeStatus: InstanceRuntimeStatus.Stopped,
            maintenanceMode: true);
        SetInstance(main, "disable", main with { MaintenanceMode = false });

        CliResult result = await _cli.RunAsync("instance", "maintenance", "disable", "main");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(["The maintenance mode of main is disabled: its next start is a normal one."]);
    }

    private void SetInstance(InstanceDto instance, string operation, InstanceDto updated)
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [instance],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/maintenance/{operation}",
            StubDaemon.Json(HttpStatusCode.Accepted, updated, CliJsonContext.Default.InstanceDto));
    }
}
