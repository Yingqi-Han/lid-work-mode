using Xunit;

namespace LidWorkMode.Tests;

public sealed class GuardEnablePolicyTests
{
    [Fact]
    public void ExistingRecoveryStateRejectsSecondEnableBeforeStateCanBeOverwritten()
    {
        Assert.True(GuardEnablePolicy.CanEnable(recoveryStateExists: false));
        Assert.False(GuardEnablePolicy.CanEnable(recoveryStateExists: true));
        Assert.Equal(66, GuardEnablePolicy.ExistingRecoveryStateExitCode);
    }
}
