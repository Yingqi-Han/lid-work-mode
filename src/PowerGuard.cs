using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LidWorkMode
{
    [DataContract]
    internal sealed class GuardState
    {
        [DataMember] public int schemaVersion = 1;
        [DataMember] public Guid schemeGuid;
        [DataMember] public uint lidAc;
        [DataMember] public uint lidDc;
        [DataMember] public uint sleepAc;
        [DataMember] public uint sleepDc;
        [DataMember] public bool ac;
        [DataMember] public bool dc;
        [DataMember] public bool preventIdleSleep;
        [DataMember] public int ownerProcessId;
        [DataMember] public DateTime createdUtc;
    }

    internal static class GuardStore
    {
        public static void PrepareDirectory()
        {
            Directory.CreateDirectory(GuardPaths.DataDirectory);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(GuardPaths.DataDirectory, security);
        }

        public static void Save(GuardState state)
        {
            PrepareDirectory();
            string temp = GuardPaths.StateFile + ".tmp";
            using (FileStream stream = File.Create(temp)) new DataContractJsonSerializer(typeof(GuardState)).WriteObject(stream, state);
            if (File.Exists(GuardPaths.StateFile)) File.Replace(temp, GuardPaths.StateFile, null); else File.Move(temp, GuardPaths.StateFile);
        }

        public static GuardState Load()
        {
            using (FileStream stream = File.OpenRead(GuardPaths.StateFile))
            {
                GuardState state = (GuardState)new DataContractJsonSerializer(typeof(GuardState)).ReadObject(stream);
                if (state == null || state.schemaVersion != 1) throw new SerializationException("Unsupported recovery state schema.");
                return state;
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
                if (args.Length == 0) return 64;
                string command = args[0].ToLowerInvariant();
                if (command == "self-test") { PowerPlanService.ReadCurrent(); return 0; }
                if (command == "status") return File.Exists(GuardPaths.StateFile) ? 10 : 0;
                if (command == "recover") { Recover(); return 0; }
                if (command == "install") { Install(); return 0; }
                if (command == "enable") return Enable(args);
                return 64;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "YingqiTools-PowerGuard-error.log"), ex.ToString()); } catch { }
                return 1;
            }
        }

        private static int Enable(string[] args)
        {
            if (args.Length != 5) return 64;
            bool ac = ParseFlag(args[1]); bool dc = ParseFlag(args[2]); bool idle = ParseFlag(args[3]);
            int ownerPid; if (!int.TryParse(args[4], out ownerPid) || ownerPid <= 0 || (!ac && !dc)) return 64;
            Process owner; try { owner = Process.GetProcessById(ownerPid); } catch { return 65; }
            PowerPlanSnapshot original = PowerPlanService.ReadCurrent();
            EnableOptions options = new EnableOptions { Ac = ac, Dc = dc, PreventIdleSleep = idle };
            GuardState state = new GuardState { schemeGuid = original.SchemeGuid, lidAc = original.LidAc, lidDc = original.LidDc, sleepAc = original.SleepAc, sleepDc = original.SleepDc, ac = ac, dc = dc, preventIdleSleep = idle, ownerProcessId = ownerPid, createdUtc = DateTime.UtcNow };
            GuardStore.Save(state);
            try { PowerPlanService.Apply(original, options); }
            catch { try { PowerPlanService.Restore(original, options); } finally { GuardStore.Delete(); } throw; }

            EventWaitHandle stopEvent = CreateStopEvent();
            try
            {
                while (true)
                {
                    if (stopEvent.WaitOne(500)) break;
                    if (owner.HasExited) break;
                    if (PowerPlanService.GetActiveScheme() != original.SchemeGuid) break;
                }
            }
            finally { stopEvent.Dispose(); Recover(); owner.Dispose(); }
            return 0;
        }

        private static EventWaitHandle CreateStopEvent()
        {
            EventWaitHandleSecurity security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            bool created;
            return new EventWaitHandle(false, EventResetMode.AutoReset, GuardPaths.StopEventName, out created, security);
        }

        private static void Recover()
        {
            if (!File.Exists(GuardPaths.StateFile)) return;
            GuardState state = GuardStore.Load();
            PowerPlanSnapshot original = new PowerPlanSnapshot { SchemeGuid = state.schemeGuid, LidAc = state.lidAc, LidDc = state.lidDc, SleepAc = state.sleepAc, SleepDc = state.sleepDc };
            EnableOptions options = new EnableOptions { Ac = state.ac, Dc = state.dc, PreventIdleSleep = state.preventIdleSleep };
            PowerPlanService.Restore(original, options);
            GuardStore.Delete();
        }

        private static void Install()
        {
            GuardStore.PrepareDirectory();
            Directory.CreateDirectory(GuardPaths.InstallDirectory);
            string current = Process.GetCurrentProcess().MainModule.FileName;
            if (!string.Equals(current, GuardPaths.InstalledExe, StringComparison.OrdinalIgnoreCase)) File.Copy(current, GuardPaths.InstalledExe, true);
            string arguments = "/Create /F /TN \"YingqiTools-PowerGuard-Recover\" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR \"\\\"" + GuardPaths.InstalledExe + "\\\" recover\"";
            Process process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
            process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Failed to create recovery task.");
            Process verify = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"YingqiTools-PowerGuard-Recover\"") { UseShellExecute = false, CreateNoWindow = true });
            verify.WaitForExit(); if (verify.ExitCode != 0) throw new InvalidOperationException("Recovery task verification failed.");
        }

        private static bool ParseFlag(string value) { if (value == "1") return true; if (value == "0") return false; throw new ArgumentException("Boolean flags must be 0 or 1."); }
    }
}
