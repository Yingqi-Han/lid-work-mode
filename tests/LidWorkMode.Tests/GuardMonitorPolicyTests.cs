using Xunit;

namespace LidWorkMode.Tests;

public sealed class GuardMonitorPolicyTests
{
    private static readonly Guid Managed = Guid.Parse("4b607d49-78c4-4638-b1f7-4ca7e74b0383");

    [Fact]
    public void Evaluate_ContinuesOnlyWhileOwnerAndPlanAreStable()
    {
        Assert.Equal(GuardMonitorDecision.Continue, GuardMonitorPolicy.Evaluate(false, false, Managed, Managed));
        Assert.Equal(GuardMonitorDecision.StopRequested, GuardMonitorPolicy.Evaluate(true, false, Managed, Managed));
        Assert.Equal(GuardMonitorDecision.OwnerExited, GuardMonitorPolicy.Evaluate(false, true, Managed, Managed));
        Assert.Equal(GuardMonitorDecision.ActiveSchemeChanged, GuardMonitorPolicy.Evaluate(false, false, Guid.NewGuid(), Managed));
    }

    [Fact]
    public void Evaluate_UsesDeterministicSafetyPrecedence()
    {
        Assert.Equal(GuardMonitorDecision.StopRequested, GuardMonitorPolicy.Evaluate(true, true, Guid.NewGuid(), Managed));
        Assert.Equal(GuardMonitorDecision.OwnerExited, GuardMonitorPolicy.Evaluate(false, true, Guid.NewGuid(), Managed));
    }
}
