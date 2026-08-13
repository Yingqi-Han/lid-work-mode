using Xunit;

namespace LidWorkMode.Tests;

public sealed class PowerPlanMutationPlannerTests
{
    private static readonly Guid Scheme = Guid.Parse("4b607d49-78c4-4638-b1f7-4ca7e74b0383");

    [Fact]
    public void EnableOptions_DefaultsToNoSelectedSupply()
    {
        EnableOptions options = new();

        Assert.False(options.Ac);
        Assert.False(options.Dc);
        Assert.False(options.PreventIdleSleep);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void LidWorkModeState_DistinguishesReadyFromPendingRecovery(
        bool stateExists,
        bool readyExists,
        bool expectedActive,
        bool expectedRequiresRecovery)
    {
        LidWorkModeState state = LidWorkModeState.FromFiles(stateExists, readyExists);

        Assert.Equal(expectedActive, state.IsActive);
        Assert.Equal(expectedRequiresRecovery, state.RequiresRecovery);
    }

    [Theory]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, false, 2)]
    [InlineData(true, false, true, 2)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, true, true, 4)]
    public void CreateApply_MapsOnlySelectedSuppliesAndSettings(bool ac, bool dc, bool idle, int expectedCount)
    {
        PowerPlanSnapshot snapshot = CreateSnapshot();
        EnableOptions options = new() { Ac = ac, Dc = dc, PreventIdleSleep = idle };

        IReadOnlyList<PowerSettingWrite> writes = PowerPlanMutationPlanner.CreateApply(snapshot, options);

        Assert.Equal(expectedCount, writes.Count);
        Assert.All(writes, write => Assert.Equal(0u, write.Value));
        Assert.Equal(ac, writes.Any(write => write.Supply == PowerSupply.Ac && write.Setting == PowerPlanService.LidAction));
        Assert.Equal(dc, writes.Any(write => write.Supply == PowerSupply.Dc && write.Setting == PowerPlanService.LidAction));
        Assert.Equal(ac && idle, writes.Any(write => write.Supply == PowerSupply.Ac && write.Setting == PowerPlanService.StandbyIdle));
        Assert.Equal(dc && idle, writes.Any(write => write.Supply == PowerSupply.Dc && write.Setting == PowerPlanService.StandbyIdle));
    }

    [Fact]
    public void CreateRestore_PreservesEveryOriginalValueExactly()
    {
        PowerPlanSnapshot snapshot = CreateSnapshot();
        EnableOptions options = new() { Ac = true, Dc = true, PreventIdleSleep = true };

        IReadOnlyList<PowerSettingWrite> writes = PowerPlanMutationPlanner.CreateRestore(snapshot, options);

        Assert.Collection(
            writes,
            write => AssertWrite(write, PowerSupply.Ac, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction, 1),
            write => AssertWrite(write, PowerSupply.Dc, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction, 2),
            write => AssertWrite(write, PowerSupply.Ac, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle, 3600),
            write => AssertWrite(write, PowerSupply.Dc, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle, 180));
    }

    [Fact]
    public void CreateRestore_IdleFlagWithoutSupplyDoesNotCreateWrites()
    {
        IReadOnlyList<PowerSettingWrite> writes = PowerPlanMutationPlanner.CreateRestore(
            CreateSnapshot(),
            new EnableOptions { PreventIdleSleep = true });

        Assert.Empty(writes);
    }

    private static PowerPlanSnapshot CreateSnapshot() => new()
    {
        SchemeGuid = Scheme,
        LidAc = 1,
        LidDc = 2,
        SleepAc = 3600,
        SleepDc = 180
    };

    private static void AssertWrite(PowerSettingWrite actual, PowerSupply supply, Guid subgroup, Guid setting, uint value)
    {
        Assert.Equal(supply, actual.Supply);
        Assert.Equal(subgroup, actual.Subgroup);
        Assert.Equal(setting, actual.Setting);
        Assert.Equal(value, actual.Value);
    }
}
