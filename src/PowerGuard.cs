using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LidWorkMode
{
    internal enum GuardCommand
    {
        SelfTest,
        Status,
        Recover,
        Install,
        Enable
    }

    internal readonly record struct GuardInvocation(
        GuardCommand Command,
        EnableOptions? Options = null,
        int OwnerProcessId = 0);

    internal static class GuardCommandParser
    {
        public const int InvalidArgumentsExitCode = 64;

        public static bool TryParse(string[] args, out GuardInvocation invocation)
        {
            invocation = default;
            if (args is null || args.Length == 0) return false;

            switch (args[0].ToLowerInvariant())
            {
                case "self-test" when args.Length == 1:
                    invocation = new GuardInvocation(GuardCommand.SelfTest);
                    return true;
                case "status" when args.Length == 1:
                    invocation = new GuardInvocation(GuardCommand.Status);
                    return true;
                case "recover" when args.Length == 1:
                    invocation = new GuardInvocation(GuardCommand.Recover);
                    return true;
                case "install" when args.Length == 1:
                    invocation = new GuardInvocation(GuardCommand.Install);
                    return true;
                case "enable" when args.Length == 5:
                    return TryParseEnable(args, out invocation);
                default:
                    return false;
            }
        }

        private static bool TryParseEnable(string[] args, out GuardInvocation invocation)
        {
            invocation = default;
            if (!TryParseFlag(args[1], out bool ac) ||
                !TryParseFlag(args[2], out bool dc) ||
                !TryParseFlag(args[3], out bool idle) ||
                !int.TryParse(args[4], out int ownerPid) ||
                ownerPid <= 0 ||
                (!ac && !dc))
            {
                return false;
            }

            invocation = new GuardInvocation(
                GuardCommand.Enable,
                new EnableOptions { Ac = ac, Dc = dc, PreventIdleSleep = idle },
                ownerPid);
            return true;
        }

        private static bool TryParseFlag(string value, out bool result)
        {
            if (value == "1") { result = true; return true; }
            if (value == "0") { result = false; return true; }
            result = false;
            return false;
        }
    }

    internal enum GuardMonitorDecision
    {
        Continue,
        StopRequested,
        OwnerExited,
        ActiveSchemeChanged
    }

    internal static class GuardMonitorPolicy
    {
        public static GuardMonitorDecision Evaluate(bool stopRequested, bool ownerHasExited, Guid activeScheme, Guid managedScheme)
        {
            if (stopRequested) return GuardMonitorDecision.StopRequested;
            if (ownerHasExited) return GuardMonitorDecision.OwnerExited;
            if (activeScheme != managedScheme) return GuardMonitorDecision.ActiveSchemeChanged;
            return GuardMonitorDecision.Continue;
        }
    }

    internal static class GuardEnablePolicy
    {
        public const int ExistingRecoveryStateExitCode = 66;

        public static bool CanEnable(bool recoveryStateExists) => !recoveryStateExists;
    }

    internal interface IGuardStateRepository
    {
        bool Exists { get; }
        void Save(GuardState state);
        GuardState Load();
        void Delete();
    }

    internal interface IGuardReadySignal
    {
        bool IsSet { get; }
        void Reset();
        void Set();
    }

    internal enum GuardEnableOutcome
    {
        Ready,
        ApplyFailedRestored,
        ApplyFailedRecoveryPending,
        VerificationFailedRestored,
        VerificationFailedRecoveryPending
    }

    internal sealed class PowerGuardCoordinator
    {
        private readonly IPowerPlanBackend _power;
        private readonly IGuardStateRepository _states;
        private readonly IGuardReadySignal _ready;

        public PowerGuardCoordinator(IPowerPlanBackend power, IGuardStateRepository states, IGuardReadySignal ready)
        {
            _power = power;
            _states = states;
            _ready = ready;
        }

        public GuardEnableOutcome Enable(PowerPlanSnapshot original, EnableOptions options, int ownerProcessId, DateTime createdUtc)
        {
            _ready.Reset();
            _states.Save(GuardStore.CreateState(original, options, ownerProcessId, createdUtc));
            bool applyCompleted = false;
            try
            {
                PowerPlanService.Apply(_power, original, options);
                applyCompleted = true;
            }
            catch
            {
                return TryRestoreAndDelete(original, options, out _)
                    ? GuardEnableOutcome.ApplyFailedRestored
                    : GuardEnableOutcome.ApplyFailedRecoveryPending;
            }

            bool applyVerified;
            try
            {
                applyVerified = PowerPlanVerifier.MatchesManagedValues(
                    _power.Read(original.SchemeGuid),
                    original.SchemeGuid,
                    PowerPlanMutationPlanner.CreateApply(original, options));
            }
            catch
            {
                applyVerified = false;
            }

            if (!applyCompleted || !applyVerified)
            {
                return TryRestoreAndDelete(original, options, out _)
                    ? GuardEnableOutcome.VerificationFailedRestored
                    : GuardEnableOutcome.VerificationFailedRecoveryPending;
            }

            _ready.Set();
            return GuardEnableOutcome.Ready;
        }

        public bool Recover()
        {
            if (!_states.Exists)
            {
                TryResetReady();
                return true;
            }
            try
            {
                GuardState state = _states.Load();
                (PowerPlanSnapshot original, EnableOptions options) = GuardStore.CreateRecovery(state);
                TryResetReady();
                return TryRestoreAndDelete(original, options, out _);
            }
            catch
            {
                return false;
            }
        }

        internal bool TryRestoreAndDelete(PowerPlanSnapshot original, EnableOptions options, out bool cleanupPending)
        {
            cleanupPending = false;
            try
            {
                PowerPlanService.Restore(_power, original, options);
                PowerPlanSnapshot actual = _power.Read(original.SchemeGuid);
                if (!PowerPlanVerifier.MatchesManagedValues(actual, original.SchemeGuid, PowerPlanMutationPlanner.CreateRestore(original, options))) return false;
                TryResetReady();
                try { _states.Delete(); }
                catch { cleanupPending = true; }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void TryResetReady()
        {
            try { _ready.Reset(); }
            catch { }
        }
    }

    internal static class PowerPlanVerifier
    {
        public static bool MatchesManagedValues(PowerPlanSnapshot actual, Guid expectedScheme, IReadOnlyList<PowerSettingWrite> expectedWrites)
        {
            if (actual.SchemeGuid != expectedScheme) return false;
            foreach (PowerSettingWrite write in expectedWrites)
            {
                uint actualValue = (write.Supply, write.Setting) switch
                {
                    (PowerSupply.Ac, var setting) when setting == PowerPlanService.LidAction => actual.LidAc,
                    (PowerSupply.Dc, var setting) when setting == PowerPlanService.LidAction => actual.LidDc,
                    (PowerSupply.Ac, var setting) when setting == PowerPlanService.StandbyIdle => actual.SleepAc,
                    (PowerSupply.Dc, var setting) when setting == PowerPlanService.StandbyIdle => actual.SleepDc,
                    _ => uint.MaxValue
                };
                if (actualValue != write.Value) return false;
            }
            return true;
        }
    }

    [DataContract]
    internal sealed class GuardState
    {
        [DataMember(IsRequired = true)] public int schemaVersion = 1;
        [DataMember(IsRequired = true)] public Guid schemeGuid;
        [DataMember(IsRequired = true)] public uint lidAc;
        [DataMember(IsRequired = true)] public uint lidDc;
        [DataMember(IsRequired = true)] public uint sleepAc;
        [DataMember(IsRequired = true)] public uint sleepDc;
        [DataMember(IsRequired = true)] public bool ac;
        [DataMember(IsRequired = true)] public bool dc;
        [DataMember(IsRequired = true)] public bool preventIdleSleep;
        [DataMember(IsRequired = true)] public int ownerProcessId;
        [DataMember(IsRequired = true)] public DateTime createdUtc;
    }

    internal static class GuardStateValidator
    {
        public static bool TryValidate(GuardState? state, out string error)
        {
            if (state is null) return Fail("Recovery state is missing.", out error);
            if (state.schemaVersion != 1) return Fail("Unsupported recovery state schema.", out error);
            if (state.schemeGuid == Guid.Empty) return Fail("Recovery state has an invalid power scheme.", out error);
            if (state.ownerProcessId <= 0) return Fail("Recovery state has an invalid owner process.", out error);
            if (state.createdUtc == default) return Fail("Recovery state has an invalid creation time.", out error);
            if (!state.ac && !state.dc) return Fail("Recovery state does not select AC or DC power.", out error);
            if (state.lidAc > 3 || state.lidDc > 3) return Fail("Recovery state contains an invalid lid action.", out error);

            (PowerPlanSnapshot snapshot, EnableOptions options) = GuardStore.CreateRecoveryUnchecked(state);
            IReadOnlyList<PowerSettingWrite> apply = PowerPlanMutationPlanner.CreateApply(snapshot, options);
            IReadOnlyList<PowerSettingWrite> restore = PowerPlanMutationPlanner.CreateRestore(snapshot, options);
            if (apply.Count == 0 || apply.Count != restore.Count) return Fail("Recovery state has inconsistent write sets.", out error);
            for (int index = 0; index < apply.Count; index++)
            {
                if (apply[index].Subgroup != restore[index].Subgroup ||
                    apply[index].Setting != restore[index].Setting ||
                    apply[index].Supply != restore[index].Supply ||
                    apply[index].Value != 0)
                {
                    return Fail("Recovery state contains an unsafe write plan.", out error);
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }

    internal sealed class FileGuardStateRepository : IGuardStateRepository
    {
        public bool Exists => File.Exists(GuardPaths.StateFile);
        public void Save(GuardState state) => GuardStore.Save(state);
        public GuardState Load() => GuardStore.Load();
        public void Delete() => GuardStore.Delete();
    }

    internal sealed class FileGuardReadySignal : IGuardReadySignal
    {
        public bool IsSet => File.Exists(GuardPaths.ReadyFile);

        public void Reset()
        {
            if (File.Exists(GuardPaths.ReadyFile)) File.Delete(GuardPaths.ReadyFile);
            string temp = GuardPaths.ReadyFile + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }

        public void Set()
        {
            GuardStore.PrepareDirectory();
            string temp = GuardPaths.ReadyFile + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.Write("ready");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, GuardPaths.ReadyFile);
        }
    }

    internal static class GuardStore
    {
        private static readonly IReadOnlySet<string> StateMemberNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "schemeGuid", "lidAc", "lidDc", "sleepAc", "sleepDc",
            "ac", "dc", "preventIdleSleep", "ownerProcessId", "createdUtc"
        };

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(GuardState))]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "GuardState is the only data contract and all of its members are explicitly preserved.")]
        public static void Serialize(Stream stream, GuardState state)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(state);
            new DataContractJsonSerializer(typeof(GuardState)).WriteObject(stream, state);
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(GuardState))]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "GuardState is the only data contract and all of its members are explicitly preserved.")]
        public static GuardState Deserialize(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] payload = copy.ToArray();
            ValidateMemberNames(payload);
            using MemoryStream deserializeStream = new MemoryStream(payload, writable: false);
            GuardState? state = (GuardState?)new DataContractJsonSerializer(typeof(GuardState)).ReadObject(deserializeStream);
            if (!GuardStateValidator.TryValidate(state, out string error)) throw new SerializationException(error);
            return state!;
        }

        private static void ValidateMemberNames(byte[] payload)
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new SerializationException("Recovery state must be a JSON object.");
            foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!StateMemberNames.Contains(property.Name))
                    throw new SerializationException("Recovery state contains unsupported fields.");
            }
        }

        internal static void VerifySerializationRoundTrip()
        {
            DateTime createdUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            GuardState expected = CreateState(
                new PowerPlanSnapshot
                {
                    SchemeGuid = new Guid("4b607d49-78c4-4638-b1f7-4ca7e74b0383"),
                    LidAc = 1,
                    LidDc = 2,
                    SleepAc = 0,
                    SleepDc = 180
                },
                new EnableOptions { Ac = true, Dc = true, PreventIdleSleep = true },
                42,
                createdUtc);
            using MemoryStream stream = new MemoryStream();
            Serialize(stream, expected);
            stream.Position = 0;
            GuardState actual = Deserialize(stream);
            if (actual.schemaVersion != expected.schemaVersion ||
                actual.schemeGuid != expected.schemeGuid ||
                actual.lidAc != expected.lidAc ||
                actual.lidDc != expected.lidDc ||
                actual.sleepAc != expected.sleepAc ||
                actual.sleepDc != expected.sleepDc ||
                actual.ac != expected.ac ||
                actual.dc != expected.dc ||
                actual.preventIdleSleep != expected.preventIdleSleep ||
                actual.ownerProcessId != expected.ownerProcessId ||
                actual.createdUtc != expected.createdUtc)
            {
                throw new InvalidOperationException("Recovery state serialization self-test failed.");
            }
        }

        public static GuardState CreateState(PowerPlanSnapshot original, EnableOptions options, int ownerProcessId, DateTime createdUtc)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(options);
            return new GuardState
            {
                schemeGuid = original.SchemeGuid,
                lidAc = original.LidAc,
                lidDc = original.LidDc,
                sleepAc = original.SleepAc,
                sleepDc = original.SleepDc,
                ac = options.Ac,
                dc = options.Dc,
                preventIdleSleep = options.PreventIdleSleep,
                ownerProcessId = ownerProcessId,
                createdUtc = createdUtc
            };
        }

        public static (PowerPlanSnapshot Snapshot, EnableOptions Options) CreateRecovery(GuardState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (!GuardStateValidator.TryValidate(state, out string error)) throw new SerializationException(error);
            return CreateRecoveryUnchecked(state);
        }

        internal static (PowerPlanSnapshot Snapshot, EnableOptions Options) CreateRecoveryUnchecked(GuardState state)
        {
            return (
                new PowerPlanSnapshot
                {
                    SchemeGuid = state.schemeGuid,
                    LidAc = state.lidAc,
                    LidDc = state.lidDc,
                    SleepAc = state.sleepAc,
                    SleepDc = state.sleepDc
                },
                new EnableOptions
                {
                    Ac = state.ac,
                    Dc = state.dc,
                    PreventIdleSleep = state.preventIdleSleep
                });
        }

        public static void PrepareDirectory()
        {
            Directory.CreateDirectory(GuardPaths.DataDirectory);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(GuardPaths.DataDirectory).SetAccessControl(security);
        }

        public static void Save(GuardState state)
        {
            PrepareDirectory();
            SaveExclusive(GuardPaths.StateFile, state);
        }

        internal static void SaveExclusive(string stateFile, GuardState state)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
            ArgumentNullException.ThrowIfNull(state);
            string? directory = Path.GetDirectoryName(stateFile);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("Recovery state directory does not exist.");

            string temp = Path.Combine(directory, Path.GetFileName(stateFile) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Serialize(stream, state);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, stateFile, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        public static GuardState Load()
        {
            using (FileStream stream = File.OpenRead(GuardPaths.StateFile))
            {
                return Deserialize(stream);
            }
        }

        public static void Delete() { if (File.Exists(GuardPaths.StateFile)) File.Delete(GuardPaths.StateFile); }
    }

    internal static class PowerGuardProgram
    {
        private static int Main(string[] args)
        {
            try
            {
                if (!GuardCommandParser.TryParse(args, out GuardInvocation invocation)) return GuardCommandParser.InvalidArgumentsExitCode;
                if (invocation.Command == GuardCommand.SelfTest) { GuardStore.VerifySerializationRoundTrip(); PowerPlanService.ReadCurrent(); return 0; }
                if (invocation.Command == GuardCommand.Status) return File.Exists(GuardPaths.StateFile) ? 10 : 0;
                if (invocation.Command == GuardCommand.Recover) { Recover(); return 0; }
                if (invocation.Command == GuardCommand.Install) { Install(); return 0; }
                if (invocation.Command == GuardCommand.Enable) return Enable(invocation);
                return GuardCommandParser.InvalidArgumentsExitCode;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "YingqiTools-PowerGuard-error.log"), ex.ToString()); } catch { }
                return 1;
            }
        }

        private static int Enable(GuardInvocation invocation)
        {
            if (!GuardEnablePolicy.CanEnable(File.Exists(GuardPaths.StateFile))) return GuardEnablePolicy.ExistingRecoveryStateExitCode;
            EnableOptions options = invocation.Options!;
            int ownerPid = invocation.OwnerProcessId;
            Process owner; try { owner = Process.GetProcessById(ownerPid); } catch { return 65; }
            IPowerPlanBackend power = new NativePowerPlanBackend();
            PowerPlanSnapshot original = power.Read(power.GetActiveScheme());
            FileGuardStateRepository states = new FileGuardStateRepository();
            FileGuardReadySignal ready = new FileGuardReadySignal();
            PowerGuardCoordinator coordinator = new PowerGuardCoordinator(power, states, ready);
            GuardEnableOutcome outcome = coordinator.Enable(original, options, ownerPid, DateTime.UtcNow);
            if (outcome != GuardEnableOutcome.Ready)
            {
                owner.Dispose();
                return outcome is GuardEnableOutcome.ApplyFailedRestored or GuardEnableOutcome.VerificationFailedRestored ? 1 : 67;
            }

            EventWaitHandle stopEvent = CreateStopEvent();
            try
            {
                while (true)
                {
                    bool stopRequested = stopEvent.WaitOne(500);
                    Guid activeScheme = stopRequested || owner.HasExited
                        ? original.SchemeGuid
                        : PowerPlanService.GetActiveScheme();
                    if (GuardMonitorPolicy.Evaluate(stopRequested, owner.HasExited, activeScheme, original.SchemeGuid) != GuardMonitorDecision.Continue) break;
                }
            }
            finally { stopEvent.Dispose(); coordinator.Recover(); owner.Dispose(); }
            return 0;
        }

        private static EventWaitHandle CreateStopEvent()
        {
            EventWaitHandleSecurity security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            bool created;
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, GuardPaths.StopEventName, out created, security);
        }

        private static void Recover()
        {
            PowerGuardCoordinator coordinator = new PowerGuardCoordinator(new NativePowerPlanBackend(), new FileGuardStateRepository(), new FileGuardReadySignal());
            if (!coordinator.Recover()) throw new InvalidOperationException("Power settings could not be fully restored and verified.");
        }

        private static void Install()
        {
            GuardStore.PrepareDirectory();
            Directory.CreateDirectory(GuardPaths.InstallDirectory);
            string current = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve the PowerGuard executable path.");
            if (!string.Equals(current, GuardPaths.InstalledExe, StringComparison.OrdinalIgnoreCase)) File.Copy(current, GuardPaths.InstalledExe, true);
            string arguments = "/Create /F /TN \"YingqiTools-PowerGuard-Recover\" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR \"\\\"" + GuardPaths.InstalledExe + "\\\" recover\"";
            using Process process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
            process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Failed to create recovery task.");
            using Process verify = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"YingqiTools-PowerGuard-Recover\"") { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Failed to start schtasks.exe verification.");
            verify.WaitForExit(); if (verify.ExitCode != 0) throw new InvalidOperationException("Recovery task verification failed.");
        }

    }
}
