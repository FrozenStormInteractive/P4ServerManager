using System.Net;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Api;

public sealed class DaemonClientInstanceExtensionsTests
{
    private readonly StubDaemon _daemon = new();

    [Fact]
    public async Task Resolve_FindsAnInstanceByName()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstances(CliRunner.Instance("other"), main);

        InstanceDto resolved = await ResolveAsync("main");

        resolved.ShouldBe(main);
    }

    [Fact]
    public async Task Resolve_FindsAnInstanceByIdentifier()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetInstances(CliRunner.Instance("other"), main);

        InstanceDto resolved = await ResolveAsync(main.Id.ToString());

        resolved.ShouldBe(main);
    }

    [Fact]
    public async Task Resolve_AcceptsAnotherCaseWhenItMatchesOneInstance()
    {
        InstanceDto main = CliRunner.Instance("Main");
        SetInstances(main);

        InstanceDto resolved = await ResolveAsync("main");

        resolved.ShouldBe(main);
    }

    [Fact]
    public async Task Resolve_RefusesAnotherCaseThatMatchesSeveralInstances()
    {
        SetInstances(CliRunner.Instance("Main"), CliRunner.Instance("MAIN"));

        CliException exception = await Should.ThrowAsync<CliException>(() => ResolveAsync("main"));

        exception.Message.ShouldContain("Several instances match main");
    }

    [Fact]
    public async Task Resolve_FailsWhenNoInstanceMatches()
    {
        SetInstances(CliRunner.Instance("main"));

        CliException exception = await Should.ThrowAsync<CliException>(() => ResolveAsync("missing"));

        exception.Message.ShouldBe("No instance is named missing.");
    }

    private void SetInstances(params InstanceDto[] instances)
    {
        _daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                instances,
                CliJsonContext.Default.IReadOnlyListInstanceDto));
    }

    private async Task<InstanceDto> ResolveAsync(string reference)
    {
        using DaemonClient client = new(new HttpClient(_daemon) { BaseAddress = new Uri("http://localhost/") }, "test.sock");
        return await client.ResolveInstanceAsync(reference, TestContext.Current.CancellationToken);
    }
}
