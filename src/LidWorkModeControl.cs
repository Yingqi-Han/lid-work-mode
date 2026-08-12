using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LidWorkMode
{
    public sealed class LidWorkModeControl : UserControl
    {
        private readonly CheckBox _ac = new CheckBox();
        private readonly CheckBox _dc = new CheckBox();
        private readonly CheckBox _idle = new CheckBox();
        private readonly Label _details = new Label();
        private readonly Label _status = new Label();
        private readonly Button _enable = new Button();
        private readonly Button _restore = new Button();
        private Process _guard;
        private bool _active;

        public bool IsActive { get { return _active || File.Exists(GuardPaths.StateFile); } }
        public LidWorkModeControl()
        {
            Dock = DockStyle.Fill; BackColor = Color.White; Font = new Font("Microsoft YaHei UI", 10F);
            AddLabel("\u5408\u76d6\u7ee7\u7eed\u8fd0\u884c", 28, 22, 450, 42, 20F, FontStyle.Bold, Color.FromArgb(17, 24, 39));
            AddLabel("\u4ec5\u5728 Yingqi Tools \u672c\u6b21\u8fd0\u884c\u671f\u95f4\u751f\u6548\uff0c\u5173\u95ed工具\u7bb1\u540e\u6062\u590d\u539f\u8bbe\u7f6e\u3002", 31, 65, 560, 30, 10F, FontStyle.Regular, Color.DimGray);
            _ac.Text = "\u63d2\u7535\u65f6\u5408\u76d6\u7ee7\u7eed\u8fd0\u884c"; _ac.Checked = true; _ac.SetBounds(34, 108, 300, 30);
            _dc.Text = "\u4f7f\u7528\u7535\u6c60\u65f6也\u7ee7\u7eed\u8fd0\u884c"; _dc.SetBounds(34, 143, 310, 30);
            _idle.Text = "\u540c\u65f6\u7981\u6b62\u7a7a\u95f2\u7761\u7720"; _idle.SetBounds(34, 178, 310, 30);
            _dc.CheckedChanged += delegate { if (_dc.Checked) MessageBox.Show("\u7535\u6c60\u6a21\u5f0f\u4f1a明\u663e\u589e\u52a0\u8017\u7535\u548c\u5408\u76d6\u79ef\u70ed\u3002\n\u8bf7\u52ff\u5c06\u8fd0\u884c中\u7684\u7535\u8111\u653e\u5165\u80cc\u5305\u3001\u88ab\u8925\u6216\u4e0d\u901a\u98ce\u73af\u5883\u3002", "\u7535\u6c60\u6a21\u5f0f\u98ce\u9669", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            _details.SetBounds(34, 216, 570, 66); _details.ForeColor = Color.FromArgb(55, 65, 81);
            _enable.Text = "\u542f\u7528\u672c\u6b21\u5408\u76d6\u8fd0\u884c"; _enable.SetBounds(34, 292, 250, 48); StyleButton(_enable, Color.FromArgb(37, 99, 235)); _enable.Click += delegate { EnableMode(); };
            _restore.Text = "\u7acb\u5373\u6062\u590d\u539f\u8bbe\u7f6e"; _restore.SetBounds(300, 292, 220, 48); StyleButton(_restore, Color.FromArgb(107, 114, 128)); _restore.Enabled = false; _restore.Click += delegate { RestoreAndWait(10000); };
            _status.SetBounds(34, 354, 570, 52); _status.ForeColor = Color.FromArgb(22, 101, 52);
            Controls.AddRange(new Control[] { _ac, _dc, _idle, _details, _enable, _restore, _status });
            RefreshSnapshot();
        }

        public bool RestoreAndWait(int timeoutMilliseconds)
        {
            if (!IsActive) return true;
            try
            {
                EventWaitHandle stop = EventWaitHandle.OpenExisting(GuardPaths.StopEventName);
                stop.Set(); stop.Dispose();
                Stopwatch watch = Stopwatch.StartNew();
                while (File.Exists(GuardPaths.StateFile) && watch.ElapsedMilliseconds < timeoutMilliseconds) { Application.DoEvents(); Thread.Sleep(100); }
                _active = File.Exists(GuardPaths.StateFile);
                UpdateUi(); RefreshSnapshot(); return !_active;
            }
            catch (WaitHandleCannotBeOpenedException) { return RunRecoveryElevated(); }
        }

        private void EnableMode()
        {
            if (!_ac.Checked && !_dc.Checked) { MessageBox.Show("\u8bf7\u81f3\u5c11\u9009\u62e9\u4e00\u79cd\u4f9b\u7535\u72b6\u6001\u3002"); return; }
            if (!File.Exists(GuardPaths.InstalledExe)) { MessageBox.Show("PowerGuard \u5c1a\u672a\u5b89\u88c5\uff0c\u8bf7\u91cd\u65b0\u8fd0\u884c Yingqi Tools \u5b89\u88c5程\u5e8f\u3002"); return; }
            ProcessStartInfo info = new ProcessStartInfo(GuardPaths.InstalledExe, string.Format("enable {0} {1} {2} {3}", _ac.Checked ? 1 : 0, _dc.Checked ? 1 : 0, _idle.Checked ? 1 : 0, Process.GetCurrentProcess().Id));
            info.UseShellExecute = true; info.Verb = "runas";
            try { _guard = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode == 1223) { _status.Text = "\u5df2\u53d6\u6d88 UAC，\u672a\u4fee\u6539\u4efb\u4f55设\u7f6e\u3002"; return; } throw; }
            Stopwatch watch = Stopwatch.StartNew();
            while (!File.Exists(GuardPaths.StateFile) && !_guard.HasExited && watch.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(100); }
            _active = File.Exists(GuardPaths.StateFile);
            _status.Text = _active ? "\u5df2\u542f\u7528\u3002\u5173\u95ed Yingqi Tools \u65f6\u4f1a\u81ea\u52a8\u6062\u590d\u539f\u8bbe\u7f6e\u3002" : "\u542f\u7528\u5931\u8d25\uff0c\u7535\u6e90\u8bbe\u7f6e\u672a保持\u4fee\u6539\u3002";
            UpdateUi(); RefreshSnapshot();
        }

        private bool RunRecoveryElevated()
        {
            ProcessStartInfo info = new ProcessStartInfo(GuardPaths.InstalledExe, "recover") { UseShellExecute = true, Verb = "runas" };
            try { Process process = Process.Start(info); process.WaitForExit(10000); _active = File.Exists(GuardPaths.StateFile); UpdateUi(); RefreshSnapshot(); return !_active; }
            catch { return false; }
        }

        private void RefreshSnapshot()
        {
            try { PowerPlanSnapshot s = PowerPlanService.ReadCurrent(); _details.Text = string.Format("\u5f53\u524d\u7535\u6e90\u8ba1\u5212: {0}\n\u5408\u76d6动\u4f5c  AC: {1}   DC: {2}    \u7a7a\u95f2\u7761\u7720  AC: {3}   DC: {4}", s.SchemeGuid, ActionName(s.LidAc), ActionName(s.LidDc), SleepName(s.SleepAc), SleepName(s.SleepDc)); }
            catch (Exception ex) { _details.Text = "\u65e0\u6cd5\u8bfb\u53d6\u7535\u6e90\u8ba1\u5212: " + ex.Message; }
        }

        private void UpdateUi() { _enable.Enabled = !_active; _restore.Enabled = _active; _ac.Enabled = _dc.Enabled = _idle.Enabled = !_active; }
        private void AddLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color) { Label label = new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft }; label.SetBounds(x, y, w, h); Controls.Add(label); }
        private static void StyleButton(Button button, Color color) { button.BackColor = color; button.ForeColor = Color.White; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold); }
        private static string ActionName(uint value) { return value == 0 ? "\u4e0d\u64cd\u4f5c" : value == 1 ? "\u7761\u7720" : value == 2 ? "\u4f11\u7720" : value == 3 ? "\u5173\u673a" : value.ToString(); }
        private static string SleepName(uint value) { return value == 0 ? "\u4ece\u4e0d" : TimeSpan.FromSeconds(value).ToString(); }
    }
}
