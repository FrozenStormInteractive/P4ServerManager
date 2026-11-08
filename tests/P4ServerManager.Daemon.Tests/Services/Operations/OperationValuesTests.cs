using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Tests.Services.Operations;

public sealed class OperationValuesTests
{
    private static readonly OperationValueKey<string> Name = new("name");

    private readonly OperationValues _values = new();

    [Fact]
    public void GetRequired_ReturnsTheValueSetForTheKey()
    {
        _values.Set(Name, "main");

        _values.GetRequired(Name).ShouldBe("main");
        _values.TryGet(Name, out string? value).ShouldBeTrue();
        value.ShouldBe("main");
    }

    [Fact]
    public void GetRequired_NamesTheKeyNoStepSet()
    {
        _values.TryGet(Name, out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => _values.GetRequired(Name)).Message.ShouldContain("'name'");
    }

    [Fact]
    public void TryGet_TellsApartTwoKeysWithTheSameName()
    {
        OperationValueKey<string> other = new("name");
        _values.Set(Name, "main");

        _values.TryGet(other, out _).ShouldBeFalse();
    }
}
