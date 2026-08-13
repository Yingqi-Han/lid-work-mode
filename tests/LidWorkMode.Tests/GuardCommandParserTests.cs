using Xunit;

namespace LidWorkMode.Tests;

public sealed class GuardCommandParserTests
{
    [Theory]
    [InlineData("self-test", GuardCommand.SelfTest)]
    [InlineData("STATUS", GuardCommand.Status)]
    [InlineData("recover", GuardCommand.Recover)]
    [InlineData("install", GuardCommand.Install)]
    internal void TryParse_AcceptsOnlyFixedZeroArgumentCommands(string command, GuardCommand expected)
    {
        Assert.True(GuardCommandParser.TryParse(new[] { command }, out GuardInvocation invocation));
        Assert.Equal(expected, invocation.Command);
    }

    [Fact]
    public void TryParse_EnableMapsFlagsAndOwner()
    {
        Assert.True(GuardCommandParser.TryParse(new[] { "enable", "1", "0", "1", "123" }, out GuardInvocation invocation));

        Assert.Equal(GuardCommand.Enable, invocation.Command);
        Assert.Equal(123, invocation.OwnerProcessId);
        Assert.NotNull(invocation.Options);
        Assert.True(invocation.Options.Ac);
        Assert.False(invocation.Options.Dc);
        Assert.True(invocation.Options.PreventIdleSleep);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void TryParse_RejectsUnknownOrDangerousInput(string[] args)
    {
        Assert.False(GuardCommandParser.TryParse(args, out _));
    }

    public static TheoryData<string[]> InvalidCommands => new()
    {
        Array.Empty<string>(),
        new[] { "status", "extra" },
        new[] { "recover", "--scheme", Guid.NewGuid().ToString() },
        new[] { "enable", "1", "0", "0" },
        new[] { "enable", "1", "0", "0", "0" },
        new[] { "enable", "0", "0", "1", "123" },
        new[] { "enable", "true", "0", "0", "123" },
        new[] { "enable", "1", "0", "0", "123", "powercfg" },
        new[] { "powercfg", "/setactive", Guid.NewGuid().ToString() },
        new[] { "enable&recover", "1", "0", "0", "123" }
    };
}
