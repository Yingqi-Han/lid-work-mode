using System;
using System.Runtime.InteropServices;
using System.IO;

namespace LidWorkMode
{
    public static class GuardPaths
    {
        public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YingqiTools", "PowerGuard");
        public static readonly string StateFile = Path.Combine(DataDirectory, "recovery.json");
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
            if (options.Ac) WriteAc(original.SchemeGuid, ButtonSubgroup, LidAction, 0);
            if (options.Dc) WriteDc(original.SchemeGuid, ButtonSubgroup, LidAction, 0);
            if (options.PreventIdleSleep && options.Ac) WriteAc(original.SchemeGuid, SleepSubgroup, StandbyIdle, 0);
            if (options.PreventIdleSleep && options.Dc) WriteDc(original.SchemeGuid, SleepSubgroup, StandbyIdle, 0);
            Activate(original.SchemeGuid);
        }

        public static void Restore(PowerPlanSnapshot original, EnableOptions options)
        {
            if (options.Ac) WriteAc(original.SchemeGuid, ButtonSubgroup, LidAction, original.LidAc);
            if (options.Dc) WriteDc(original.SchemeGuid, ButtonSubgroup, LidAction, original.LidDc);
            if (options.PreventIdleSleep && options.Ac) WriteAc(original.SchemeGuid, SleepSubgroup, StandbyIdle, original.SleepAc);
            if (options.PreventIdleSleep && options.Dc) WriteDc(original.SchemeGuid, SleepSubgroup, StandbyIdle, original.SleepDc);
            if (GetActiveScheme() == original.SchemeGuid) Activate(original.SchemeGuid);
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
