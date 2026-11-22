using Kaze.Core.Services;

namespace Kaze.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parses_account_switch_with_an_action()
    {
        var r = CommandLineRequest.Parse(["--account", "home@example.com", "--power", "off"]);
        Assert.Empty(r.Errors);
        Assert.Equal("home@example.com", r.Account);
        Assert.True(r.HasUnitAction);
    }

    [Fact]
    public void Account_alone_is_not_a_unit_action()
    {
        var r = CommandLineRequest.Parse(["--account", "+66812345678"]);
        Assert.Equal("+66812345678", r.Account);
        Assert.False(r.HasUnitAction);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e300")]
    [InlineData("+NaN")]
    [InlineData("+999")]
    public void Rejects_nonsense_temperatures(string value)
    {
        var r = CommandLineRequest.Parse(["--temp", value]);
        Assert.NotEmpty(r.Errors);
    }
}
