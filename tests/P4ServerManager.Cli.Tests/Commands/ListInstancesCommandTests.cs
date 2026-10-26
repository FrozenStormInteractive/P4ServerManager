using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class ListInstancesCommandTests
{
    private readonly CliRunner _cli = new();

    [Fact]
    public async Task List_WritesATable()
    {
        SetInstances(
            CliRunner.Instance("main", port: 1666),
            CliRunner.Instance("build-farm", P4ServerInstanceState.Provisioning, InstanceRuntimeStatus.Unknown, 1667));

        CliResult result = await _cli.RunAsync("instance", "list");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "NAME        STATE         STATUS   PORT  VERSION         AUTO-START",
            "main        Ready         Running  1666  2025.1/2850123  yes",
            "build-farm  Provisioning  Unknown  1667  r25.1           yes",
        ]);
    }

    [Fact]
    public async Task List_WritesJson()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstances(main);

        CliResult result = await _cli.RunAsync("instance", "ls", "--json");

        result.ExitCode.ShouldBe(0);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement instance = document.RootElement.EnumerateArray().Single();
        instance.GetProperty("name").GetString().ShouldBe("main");
        instance.GetProperty("state").GetString().ShouldBe("Ready");
        instance.GetProperty("runtimeStatus").GetString().ShouldBe("Running");
    }

    [Fact]
    public async Task List_SaysWhenThereAreNoInstances()
    {
        SetInstances();

        CliResult result = await _cli.RunAsync("instance", "list");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldStartWith("No instances.");
    }

    [Fact]
    public async Task Socket_IsTakenFromTheOptionThenTheVariableThenTheDefault()
    {
        SetInstances();
        _cli.EnvironmentVariables[GlobalOptions.SocketVariable] = "/from/variable.sock";

        await _cli.RunAsync("instance", "list", "--socket", "/from/option.sock");
        await _cli.RunAsync("instance", "list");
        _cli.EnvironmentVariables.Clear();
        await _cli.RunAsync("instance", "list");

        _cli.SocketPaths.ShouldBe(["/from/option.sock", "/from/variable.sock", GlobalOptions.DefaultSocketPath]);
    }

    private void SetInstances(params InstanceDto[] instances)
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                instances,
                CliJsonContext.Default.IReadOnlyListInstanceDto));
    }
}
