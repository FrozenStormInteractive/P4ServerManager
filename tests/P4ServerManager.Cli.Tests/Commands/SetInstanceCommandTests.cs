using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class SetInstanceCommandTests
{
    private readonly CliRunner _cli = new();

    [Fact]
    public async Task Set_SendsTheNameAndTheDescriptionOnly()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstance(main, main with { Name = "renamed", Description = "The new text" });

        CliResult result = await _cli.RunAsync(
            "instance", "set", "main", "--name", "renamed", "--description", "The new text");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldStartWith("Updated renamed.");
        using JsonDocument request = JsonDocument.Parse(RequestBody(main));
        request.RootElement.GetProperty("name").GetString().ShouldBe("renamed");
        request.RootElement.GetProperty("description").GetString().ShouldBe("The new text");
        request.RootElement.GetProperty("autoStart").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Set_SendsAnEmptyDescriptionToRemoveIt()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstance(main, main);

        CliResult result = await _cli.RunAsync("instance", "set", "main", "--description", "");

        result.ExitCode.ShouldBe(0);
        using JsonDocument request = JsonDocument.Parse(RequestBody(main));
        request.RootElement.GetProperty("name").ValueKind.ShouldBe(JsonValueKind.Null);
        request.RootElement.GetProperty("description").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Set_AsksForAtLeastOneSetting()
    {
        CliResult result = await _cli.RunAsync("instance", "set", "main");

        result.ExitCode.ShouldNotBe(0);
        result.Error.ShouldContain("--name, --description or --auto-start");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    private void SetInstance(InstanceDto instance, InstanceDto updated)
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [instance],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Patch,
            $"/api/instances/{instance.Id}",
            StubDaemon.Json(HttpStatusCode.OK, updated, CliJsonContext.Default.InstanceDto));
    }

    private string RequestBody(InstanceDto instance)
    {
        return _cli.Daemon.RequestsTo(HttpMethod.Patch, $"/api/instances/{instance.Id}").Single().Body!;
    }
}
