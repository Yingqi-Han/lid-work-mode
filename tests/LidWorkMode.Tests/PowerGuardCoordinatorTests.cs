using Xunit;

namespace LidWorkMode.Tests;

public sealed class PowerGuardCoordinatorTests
{
    private static readonly Guid Scheme = Guid.Parse("4b607d49-78c4-4638-b1f7-4ca7e74b0383");
    private static readonly DateTime CreatedUtc = new(2026, 8, 13, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Enable_SuccessSetsReadyOnlyAfterApplyActivationAndReadbackMatch()
    {
        FakePowerBackend power = new(CreateOriginal());
        FakeStateRepository states = new();
        FakeReadySignal ready = new(() =>
        {
            Assert.True(states.Exists);
            Assert.Equal(1, power.ActivateCalls);
            Assert.Equal(0u, power.Current.LidAc);
            Assert.Equal(0u, power.Current.SleepAc);
            Assert.True(power.ReadCalls >= 1);
        });
        PowerGuardCoordinator coordinator = new(power, states, ready);

        GuardEnableOutcome outcome = coordinator.Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.Ready, outcome);
        Assert.True(ready.IsSet);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Enable_NthApplyWriteFailureDeletesStateOnlyAfterVerifiedRollback(int failWrite)
    {
        FakePowerBackend power = new(CreateOriginal()) { ThrowOnWriteCall = failWrite };
        FakeStateRepository states = new();
        FakeReadySignal ready = new();
        PowerGuardCoordinator coordinator = new(power, states, ready);

        GuardEnableOutcome outcome = coordinator.Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.ApplyFailedRestored, outcome);
        Assert.False(states.Exists);
        Assert.Equal(1, states.DeleteCalls);
        Assert.False(ready.IsSet);
        AssertManagedValuesEqual(CreateOriginal(), power.Current, AcIdle());
    }

    [Fact]
    public void Enable_ApplyFailureAndRollbackFailurePreservesRecoveryState()
    {
        FakePowerBackend power = new(CreateOriginal()) { ThrowOnWriteCalls = { 2, 3 } };
        FakeStateRepository states = new();
        FakeReadySignal ready = new();

        GuardEnableOutcome outcome = new PowerGuardCoordinator(power, states, ready).Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.ApplyFailedRecoveryPending, outcome);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
        Assert.False(ready.IsSet);
    }

    [Fact]
    public void Enable_ApplyReadbackMismatchRollsBackAndDoesNotSetReady()
    {
        FakePowerBackend power = new(CreateOriginal()) { MismatchReadCall = 1 };
        FakeStateRepository states = new();
        FakeReadySignal ready = new();

        GuardEnableOutcome outcome = new PowerGuardCoordinator(power, states, ready).Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.VerificationFailedRestored, outcome);
        Assert.False(states.Exists);
        Assert.False(ready.IsSet);
        AssertManagedValuesEqual(CreateOriginal(), power.Current, AcIdle());
    }

    [Fact]
    public void Enable_ApplyReadbackMismatchAndRollbackReadbackMismatchPreservesState()
    {
        FakePowerBackend power = new(CreateOriginal()) { MismatchReadCalls = { 1, 2 } };
        FakeStateRepository states = new();
        FakeReadySignal ready = new();

        GuardEnableOutcome outcome = new PowerGuardCoordinator(power, states, ready).Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.VerificationFailedRecoveryPending, outcome);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
        Assert.False(ready.IsSet);
    }

    [Fact]
    public void Enable_ApplyReadExceptionRollsBackAndDoesNotSetReady()
    {
        FakePowerBackend power = new(CreateOriginal()) { ThrowOnReadCall = 1 };
        FakeStateRepository states = new();
        FakeReadySignal ready = new();

        GuardEnableOutcome outcome = new PowerGuardCoordinator(power, states, ready).Enable(CreateOriginal(), AcIdle(), 42, CreatedUtc);

        Assert.Equal(GuardEnableOutcome.VerificationFailedRestored, outcome);
        Assert.False(states.Exists);
        Assert.False(ready.IsSet);
        AssertManagedValuesEqual(CreateOriginal(), power.Current, AcIdle());
    }

    [Fact]
    public void Recover_RestoreFailurePreservesState()
    {
        FakePowerBackend power = new(CreateApplied()) { ThrowOnWriteCall = 1 };
        FakeStateRepository states = FakeStateRepository.WithState(CreateOriginal(), AcIdle());

        bool recovered = new PowerGuardCoordinator(power, states, new FakeReadySignal()).Recover();

        Assert.False(recovered);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
    }

    [Fact]
    public void Recover_ReadbackMismatchPreservesState()
    {
        FakePowerBackend power = new(CreateApplied()) { MismatchReadCall = 1 };
        FakeStateRepository states = FakeStateRepository.WithState(CreateOriginal(), AcIdle());

        bool recovered = new PowerGuardCoordinator(power, states, new FakeReadySignal()).Recover();

        Assert.False(recovered);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
    }

    [Fact]
    public void Recover_VerifiedRestoreDeletesState()
    {
        FakePowerBackend power = new(CreateApplied());
        FakeStateRepository states = FakeStateRepository.WithState(CreateOriginal(), AcIdle());

        bool recovered = new PowerGuardCoordinator(power, states, new FakeReadySignal()).Recover();

        Assert.True(recovered);
        Assert.False(states.Exists);
        Assert.Equal(1, states.DeleteCalls);
        AssertManagedValuesEqual(CreateOriginal(), power.Current, AcIdle());
    }

    [Fact]
    public void Recover_StateLoadFailurePreservesStateAndReturnsFalse()
    {
        FakeStateRepository states = FakeStateRepository.WithState(CreateOriginal(), AcIdle());
        states.ThrowOnLoad = true;

        bool recovered = new PowerGuardCoordinator(new FakePowerBackend(CreateApplied()), states, new FakeReadySignal()).Recover();

        Assert.False(recovered);
        Assert.True(states.Exists);
        Assert.Equal(0, states.DeleteCalls);
    }

    [Fact]
    public void VerifiedRestore_StateDeleteFailureIsCleanupPendingButNotRestoreFailure()
    {
        FakeStateRepository states = FakeStateRepository.WithState(CreateOriginal(), AcIdle());
        states.ThrowOnDelete = true;
        PowerGuardCoordinator coordinator = new(new FakePowerBackend(CreateApplied()), states, new FakeReadySignal());

        bool restored = coordinator.TryRestoreAndDelete(CreateOriginal(), AcIdle(), out bool cleanupPending);

        Assert.True(restored);
        Assert.True(cleanupPending);
        Assert.True(states.Exists);
        Assert.Equal(1, states.DeleteCalls);
    }

    private static EnableOptions AcIdle() => new() { Ac = true, PreventIdleSleep = true };

    private static PowerPlanSnapshot CreateOriginal() => new()
    {
        SchemeGuid = Scheme,
        LidAc = 1,
        LidDc = 2,
        SleepAc = 3600,
        SleepDc = 180
    };

    private static PowerPlanSnapshot CreateApplied() => new()
    {
        SchemeGuid = Scheme,
        LidAc = 0,
        LidDc = 2,
        SleepAc = 0,
        SleepDc = 180
    };

    private static void AssertManagedValuesEqual(PowerPlanSnapshot expected, PowerPlanSnapshot actual, EnableOptions options)
    {
        Assert.True(PowerPlanVerifier.MatchesManagedValues(actual, expected.SchemeGuid, PowerPlanMutationPlanner.CreateRestore(expected, options)));
    }

    private sealed class FakeReadySignal : IGuardReadySignal
    {
        private readonly Action? _beforeSet;
        public FakeReadySignal(Action? beforeSet = null) => _beforeSet = beforeSet;
        public bool IsSet { get; private set; }
        public void Reset() => IsSet = false;
        public void Set() { _beforeSet?.Invoke(); IsSet = true; }
    }

    private sealed class FakeStateRepository : IGuardStateRepository
    {
        private GuardState? _state;
        public bool Exists => _state is not null;
        public int DeleteCalls { get; private set; }
        public bool ThrowOnLoad { get; set; }
        public bool ThrowOnDelete { get; set; }
        public void Save(GuardState state) => _state = state;
        public GuardState Load() => ThrowOnLoad ? throw new InvalidOperationException("Injected load failure.") : _state ?? throw new InvalidOperationException("No state.");
        public void Delete() { DeleteCalls++; if (ThrowOnDelete) throw new InvalidOperationException("Injected delete failure."); _state = null; }

        public static FakeStateRepository WithState(PowerPlanSnapshot original, EnableOptions options)
        {
            FakeStateRepository repository = new();
            repository.Save(GuardStore.CreateState(original, options, 42, CreatedUtc));
            return repository;
        }
    }

    private sealed class FakePowerBackend : IPowerPlanBackend
    {
        public FakePowerBackend(PowerPlanSnapshot initial) => Current = Clone(initial);
        public PowerPlanSnapshot Current { get; private set; }
        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int ThrowOnWriteCall { set { if (value > 0) ThrowOnWriteCalls.Add(value); } }
        public HashSet<int> ThrowOnWriteCalls { get; } = new();
        public int MismatchReadCall { set { if (value > 0) MismatchReadCalls.Add(value); } }
        public HashSet<int> MismatchReadCalls { get; } = new();
        public int ThrowOnReadCall { set { if (value > 0) ThrowOnReadCalls.Add(value); } }
        public HashSet<int> ThrowOnReadCalls { get; } = new();

        public PowerPlanSnapshot Read(Guid scheme)
        {
            ReadCalls++;
            if (ThrowOnReadCalls.Contains(ReadCalls)) throw new InvalidOperationException($"Injected read failure {ReadCalls}.");
            PowerPlanSnapshot result = Clone(Current);
            if (MismatchReadCalls.Contains(ReadCalls)) result.LidAc = result.LidAc == 0 ? 1u : 0u;
            return result;
        }

        public Guid GetActiveScheme() => Current.SchemeGuid;

        public void Write(Guid scheme, PowerSettingWrite write)
        {
            WriteCalls++;
            if (ThrowOnWriteCalls.Contains(WriteCalls)) throw new InvalidOperationException($"Injected write failure {WriteCalls}.");
            if (write.Setting == PowerPlanService.LidAction && write.Supply == PowerSupply.Ac) Current.LidAc = write.Value;
            else if (write.Setting == PowerPlanService.LidAction) Current.LidDc = write.Value;
            else if (write.Setting == PowerPlanService.StandbyIdle && write.Supply == PowerSupply.Ac) Current.SleepAc = write.Value;
            else if (write.Setting == PowerPlanService.StandbyIdle) Current.SleepDc = write.Value;
            else throw new InvalidOperationException("Unexpected setting.");
        }

        public void Activate(Guid scheme) { ActivateCalls++; Current.SchemeGuid = scheme; }

        private static PowerPlanSnapshot Clone(PowerPlanSnapshot value) => new()
        {
            SchemeGuid = value.SchemeGuid,
            LidAc = value.LidAc,
            LidDc = value.LidDc,
            SleepAc = value.SleepAc,
            SleepDc = value.SleepDc
        };
    }
}
