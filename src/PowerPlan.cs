using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;

namespace LidWorkMode
{
    public static class GuardPaths
    {
        public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YingqiTools", "PowerGuard");
        public static readonly string StateFile = Path.Combine(DataDirectory, "recovery.json");
        public static readonly string ReadyFile = Path.Combine(DataDirectory, "ready");
        public const string StopEventName = "Global\\YingqiTools.PowerGuard.Stop";
        public static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YingqiTools", "PowerGuard");
        public static readonly string InstalledExe = Path.Combine(InstallDirectory, "PowerGuard.exe");
    }

    public sealed class EnableOptions
    {
        public bool Ac { get; set; }
        public bool Dc { get; set; }
        public bool PreventIdleSleep { get; set; }
    }

    public sealed class PowerPlanSnapshot
    {
        public Guid SchemeGuid { get; set; }
        public uint LidAc { get; set; }
        public uint LidDc { get; set; }
        public uint SleepAc { get; set; }
        public uint SleepDc { get; set; }
    }

    public readonly record struct LidWorkModeState(bool IsActive, bool RequiresRecovery)
    {
        public static LidWorkModeState FromFiles(bool stateExists, bool readyExists) =>
            new(stateExists && readyExists, stateExists);
    }

    internal enum PowerSupply
    {
        Ac,
        Dc
    }

    internal readonly record struct PowerSettingWrite(PowerSupply Supply, Guid Subgroup, Guid Setting, uint Value);

    internal interface IPowerPlanBackend
    {
        PowerPlanSnapshot Read(Guid scheme);
        Guid GetActiveScheme();
        void Write(Guid scheme, PowerSettingWrite write);
        void Activate(Guid scheme);
    }

    internal sealed class NativePowerPlanBackend : IPowerPlanBackend
    {
        public PowerPlanSnapshot Read(Guid scheme) => new PowerPlanSnapshot
        {
            SchemeGuid = scheme,
            LidAc = PowerPlanService.ReadAcValue(scheme, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction),
            LidDc = PowerPlanService.ReadDcValue(scheme, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction),
            SleepAc = PowerPlanService.ReadAcValue(scheme, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle),
            SleepDc = PowerPlanService.ReadDcValue(scheme, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle)
        };

        public Guid GetActiveScheme() => PowerPlanService.GetActiveScheme();

        public void Write(Guid scheme, PowerSettingWrite write) => PowerPlanService.WriteValue(scheme, write);

        public void Activate(Guid scheme) => PowerPlanService.ActivateScheme(scheme);
    }

    internal static class PowerPlanMutationPlanner
    {
        public static IReadOnlyList<PowerSettingWrite> CreateApply(PowerPlanSnapshot original, EnableOptions options)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(options);

            List<PowerSettingWrite> writes = new List<PowerSettingWrite>();
            AddSelectedWrites(writes, options, 0, 0, 0, 0);
            return writes;
        }

        public static IReadOnlyList<PowerSettingWrite> CreateRestore(PowerPlanSnapshot original, EnableOptions options)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(options);

            List<PowerSettingWrite> writes = new List<PowerSettingWrite>();
            AddSelectedWrites(writes, options, original.LidAc, original.LidDc, original.SleepAc, original.SleepDc);
            return writes;
        }

        private static void AddSelectedWrites(List<PowerSettingWrite> writes, EnableOptions options, uint lidAc, uint lidDc, uint sleepAc, uint sleepDc)
        {
            if (options.Ac) writes.Add(new PowerSettingWrite(PowerSupply.Ac, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction, lidAc));
            if (options.Dc) writes.Add(new PowerSettingWrite(PowerSupply.Dc, PowerPlanService.ButtonSubgroup, PowerPlanService.LidAction, lidDc));
            if (options.PreventIdleSleep && options.Ac) writes.Add(new PowerSettingWrite(PowerSupply.Ac, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle, sleepAc));
            if (options.PreventIdleSleep && options.Dc) writes.Add(new PowerSettingWrite(PowerSupply.Dc, PowerPlanService.SleepSubgroup, PowerPlanService.StandbyIdle, sleepDc));
        }
    }

    public static class PowerPlanService
    {
        public static readonly Guid ButtonSubgroup = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        public static readonly Guid LidAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
        public static readonly Guid SleepSubgroup = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        public static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

        public static PowerPlanSnapshot ReadCurrent()
        {
            Guid scheme = GetActiveScheme();
            return new PowerPlanSnapshot
            {
                SchemeGuid = scheme,
                LidAc = ReadAc(scheme, ButtonSubgroup, LidAction),
                LidDc = ReadDc(scheme, ButtonSubgroup, LidAction),
                SleepAc = ReadAc(scheme, SleepSubgroup, StandbyIdle),
                SleepDc = ReadDc(scheme, SleepSubgroup, StandbyIdle)
            };
        }

        public static Guid GetActiveScheme()
        {
            IntPtr pointer;
            Check(PowerGetActiveScheme(IntPtr.Zero, out pointer), "PowerGetActiveScheme");
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }

        public static void Apply(PowerPlanSnapshot original, EnableOptions options)
        {
            Apply(new NativePowerPlanBackend(), original, options);
        }

        public static void Restore(PowerPlanSnapshot original, EnableOptions options)
        {
            Restore(new NativePowerPlanBackend(), original, options);
        }

        internal static void Apply(IPowerPlanBackend backend, PowerPlanSnapshot original, EnableOptions options)
        {
            foreach (PowerSettingWrite write in PowerPlanMutationPlanner.CreateApply(original, options)) backend.Write(original.SchemeGuid, write);
            backend.Activate(original.SchemeGuid);
        }

        internal static void Restore(IPowerPlanBackend backend, PowerPlanSnapshot original, EnableOptions options)
        {
            foreach (PowerSettingWrite write in PowerPlanMutationPlanner.CreateRestore(original, options)) backend.Write(original.SchemeGuid, write);
            if (backend.GetActiveScheme() == original.SchemeGuid) backend.Activate(original.SchemeGuid);
        }

        internal static uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting) => ReadAc(scheme, subgroup, setting);
        internal static uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting) => ReadDc(scheme, subgroup, setting);
        internal static void ActivateScheme(Guid scheme) => Activate(scheme);

        internal static void WriteValue(Guid scheme, PowerSettingWrite write)
        {
            if (write.Supply == PowerSupply.Ac) WriteAc(scheme, write.Subgroup, write.Setting, write.Value);
            else WriteDc(scheme, write.Subgroup, write.Setting, write.Value);
        }

        private static uint ReadAc(Guid scheme, Guid subgroup, Guid setting) { uint value; Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value), "PowerReadACValueIndex"); return value; }
        private static uint ReadDc(Guid scheme, Guid subgroup, Guid setting) { uint value; Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value), "PowerReadDCValueIndex"); return value; }
        private static void WriteAc(Guid scheme, Guid subgroup, Guid setting, uint value) { Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value), "PowerWriteACValueIndex"); }
        private static void WriteDc(Guid scheme, Guid subgroup, Guid setting, uint value) { Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value), "PowerWriteDCValueIndex"); }
        private static void Activate(Guid scheme) { Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme"); }
        private static void Check(uint result, string operation) { if (result != 0) throw new System.ComponentModel.Win32Exception((int)result, operation + " failed"); }

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr schemeGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
