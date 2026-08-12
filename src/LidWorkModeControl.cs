using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LidWorkMode
{
    internal static class LidTheme
    {
        public static readonly Color Canvas = Color.FromArgb(245, 247, 251);
        public static readonly Color Card = Color.White;
        public static readonly Color Text = Color.FromArgb(20, 30, 47);
        public static readonly Color Muted = Color.FromArgb(103, 117, 139);
        public static readonly Color Border = Color.FromArgb(224, 229, 238);
        public static readonly Color Blue = Color.FromArgb(61, 123, 253);
        public static readonly Color BlueDark = Color.FromArgb(45, 99, 220);

        public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LidCard : Panel
    {
        private Color _fillColor = LidTheme.Card;
        private Color _borderColor = LidTheme.Border;
        public Color FillColor { get { return _fillColor; } set { _fillColor = value; Invalidate(); } }
        public Color BorderColor { get { return _borderColor; } set { _borderColor = value; Invalidate(); } }
        public LidCard()
        {
            BackColor = LidTheme.Canvas;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = LidTheme.RoundedRectangle(bounds, 16))
            using (Brush fill = new SolidBrush(_fillColor))
            using (Pen border = new Pen(_borderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    internal sealed class LidRoundedButton : Button
    {
        private Color _normalColor = LidTheme.Blue;
        private Color _hoverColor = LidTheme.BlueDark;
        private bool _hovered;
        public Color NormalColor { get { return _normalColor; } set { _normalColor = value; Invalidate(); } }
        public Color HoverColor { get { return _hoverColor; } set { _hoverColor = value; Invalidate(); } }
        public LidRoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fillColor = Enabled ? (_hovered ? _hoverColor : _normalColor) : Color.FromArgb(187, 197, 211);
            using (GraphicsPath path = LidTheme.RoundedRectangle(bounds, 11))
            using (Brush brush = new SolidBrush(fillColor)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        private bool _hovered;
        public ToggleSwitch()
        {
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Size = new Size(48, 26);
            Text = string.Empty;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(1, 3, 46, 22);
            Color trackColor = Enabled ? (Checked ? LidTheme.Blue : (_hovered ? Color.FromArgb(185, 195, 209) : Color.FromArgb(207, 215, 226))) : Color.FromArgb(224, 229, 236);
            using (GraphicsPath path = LidTheme.RoundedRectangle(track, 11))
            using (Brush brush = new SolidBrush(trackColor)) e.Graphics.FillPath(brush, path);
            int x = Checked ? 25 : 4;
            using (Brush knob = new SolidBrush(Color.White)) e.Graphics.FillEllipse(knob, x, 6, 16, 16);
        }
    }

    public sealed class LidWorkModeControl : UserControl
    {
        private readonly ToggleSwitch _ac = new ToggleSwitch();
        private readonly ToggleSwitch _dc = new ToggleSwitch();
        private readonly ToggleSwitch _idle = new ToggleSwitch();
        private readonly Label _scheme = new Label();
        private readonly Label _details = new Label();
        private readonly Label _status = new Label();
        private readonly LidRoundedButton _enable = new LidRoundedButton();
        private readonly LidRoundedButton _restore = new LidRoundedButton();
        private Process _guard;
        private bool _active;

        public bool IsActive { get { return _active || File.Exists(GuardPaths.StateFile); } }

        public LidWorkModeControl()
        {
            Dock = DockStyle.Fill;
            BackColor = LidTheme.Canvas;
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScroll = true;

            Panel header = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = LidTheme.Canvas };
            Label eyebrow = MakeLabel("TEMPORARY LID MODE", 0, 0, 360, 20, 8.5F, FontStyle.Bold, LidTheme.Blue);
            Label title = MakeLabel("合盖继续运行", 0, 24, 520, 42, 25F, FontStyle.Bold, LidTheme.Text);
            Label description = MakeLabel("仅在本次工具箱运行期间生效，关闭后自动恢复原设置。", 1, 69, 680, 23, 10F, FontStyle.Regular, LidTheme.Muted);
            header.Controls.AddRange(new Control[] { eyebrow, title, description });

            LidCard stateCard = new LidCard { Dock = DockStyle.Top, Height = 116 };
            Label stateTitle = MakeLabel("当前电源状态", 24, 18, 180, 25, 10F, FontStyle.Bold, LidTheme.Text);
            Label badge = MakeLabel("待命", 0, 17, 64, 28, 8.5F, FontStyle.Bold, Color.FromArgb(65, 89, 124));
            badge.TextAlign = ContentAlignment.MiddleCenter;
            badge.BackColor = Color.FromArgb(237, 241, 247);
            badge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            badge.Left = stateCard.Width - 88;
            stateCard.Resize += delegate { badge.Left = stateCard.ClientSize.Width - 88; };
            _scheme.SetBounds(24, 46, 660, 22);
            _scheme.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _scheme.Font = new Font("Segoe UI", 8.5F);
            _scheme.ForeColor = LidTheme.Muted;
            _scheme.BackColor = Color.Transparent;
            _details.SetBounds(24, 71, 660, 34);
            _details.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _details.Font = new Font("Microsoft YaHei UI", 9F);
            _details.ForeColor = LidTheme.Text;
            _details.BackColor = Color.Transparent;
            stateCard.Controls.AddRange(new Control[] { stateTitle, badge, _scheme, _details });

            Panel gapOne = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = LidTheme.Canvas };
            LidCard optionsCard = new LidCard { Dock = DockStyle.Top, Height = 216 };
            Label optionsTitle = MakeLabel("本次启用范围", 24, 16, 260, 28, 11F, FontStyle.Bold, LidTheme.Text);
            Label optionsHelp = MakeLabel("选择要临时修改的供电状态", 24, 42, 360, 22, 8.5F, FontStyle.Regular, LidTheme.Muted);
            optionsCard.Controls.AddRange(new Control[] { optionsTitle, optionsHelp });
            AddOptionRow(optionsCard, _ac, 69, "插电时合盖继续运行", "推荐 · 适合持续任务和散热", true);
            AddOptionRow(optionsCard, _dc, 119, "使用电池时也继续运行", "会增加耗电和合盖积热", false);
            AddOptionRow(optionsCard, _idle, 169, "同时禁止空闲睡眠", "仅当任务仍会被空闲睡眠中断时使用", false);
            _dc.CheckedChanged += delegate
            {
                if (_dc.Checked)
                    MessageBox.Show("电池模式会明显增加耗电和合盖积热。\n\n请勿将运行中的电脑放入背包、被褥或不通风环境。", "电池模式风险", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            Panel gapTwo = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = LidTheme.Canvas };
            LidCard warningCard = new LidCard { Dock = DockStyle.Top, Height = 82, FillColor = Color.FromArgb(255, 249, 235), BorderColor = Color.FromArgb(245, 222, 165) };
            Label warningMark = MakeLabel("!", 24, 21, 38, 38, 16F, FontStyle.Bold, Color.FromArgb(176, 112, 23));
            warningMark.TextAlign = ContentAlignment.MiddleCenter;
            Label warningTitle = MakeLabel("注意散热", 76, 15, 180, 26, 10F, FontStyle.Bold, Color.FromArgb(121, 78, 20));
            Label warningText = MakeLabel("合盖运行时请放在通风、坚硬的表面；不要放进背包、床铺或被褥。", 76, 39, 610, 28, 8.8F, FontStyle.Regular, Color.FromArgb(143, 98, 37));
            warningText.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            warningCard.Controls.AddRange(new Control[] { warningMark, warningTitle, warningText });

            Panel actionPanel = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = LidTheme.Canvas };
            _enable.Text = "启用本次合盖运行";
            _enable.AccessibleName = "启用本次合盖运行";
            _enable.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            _enable.SetBounds(0, 18, 220, 48);
            _enable.Click += delegate { EnableMode(); };
            _restore.Text = "立即恢复原设置";
            _restore.AccessibleName = "立即恢复原设置";
            _restore.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            _restore.NormalColor = Color.FromArgb(103, 116, 135);
            _restore.HoverColor = Color.FromArgb(78, 91, 110);
            _restore.SetBounds(234, 18, 190, 48);
            _restore.Enabled = false;
            _restore.Click += delegate { RestoreAndWait(10000); };
            _status.SetBounds(445, 18, 240, 48);
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _status.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _status.ForeColor = LidTheme.Muted;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "尚未启用，不会修改电源设置";
            actionPanel.Controls.AddRange(new Control[] { _enable, _restore, _status });

            Controls.Add(actionPanel);
            Controls.Add(warningCard);
            Controls.Add(gapTwo);
            Controls.Add(optionsCard);
            Controls.Add(gapOne);
            Controls.Add(stateCard);
            Controls.Add(header);
            RefreshSnapshot();
            UpdateUi();
        }

        public bool RestoreAndWait(int timeoutMilliseconds)
        {
            if (!IsActive) return true;
            try
            {
                EventWaitHandle stop = EventWaitHandle.OpenExisting(GuardPaths.StopEventName);
                stop.Set();
                stop.Dispose();
                Stopwatch watch = Stopwatch.StartNew();
                while (File.Exists(GuardPaths.StateFile) && watch.ElapsedMilliseconds < timeoutMilliseconds) { Application.DoEvents(); Thread.Sleep(100); }
                _active = File.Exists(GuardPaths.StateFile);
                _status.Text = _active ? "恢复仍在进行，请稍候" : "已恢复为启用前的设置";
                _status.ForeColor = _active ? Color.FromArgb(184, 113, 24) : LidTheme.Muted;
                UpdateUi();
                RefreshSnapshot();
                return !_active;
            }
            catch (WaitHandleCannotBeOpenedException) { return RunRecoveryElevated(); }
        }

        private void EnableMode()
        {
            if (!_ac.Checked && !_dc.Checked) { MessageBox.Show("请至少选择一种供电状态。", "请选择启用范围", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!File.Exists(GuardPaths.InstalledExe)) { MessageBox.Show("PowerGuard 尚未安装，请重新运行 Yingqi Tools 安装程序。", "缺少 PowerGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            ProcessStartInfo info = new ProcessStartInfo(GuardPaths.InstalledExe, string.Format("enable {0} {1} {2} {3}", _ac.Checked ? 1 : 0, _dc.Checked ? 1 : 0, _idle.Checked ? 1 : 0, Process.GetCurrentProcess().Id));
            info.UseShellExecute = true;
            info.Verb = "runas";
            try { _guard = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) { _status.Text = "已取消 UAC，没有修改任何设置"; _status.ForeColor = LidTheme.Muted; return; }
                throw;
            }
            Stopwatch watch = Stopwatch.StartNew();
            while (!File.Exists(GuardPaths.StateFile) && !_guard.HasExited && watch.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(100); }
            _active = File.Exists(GuardPaths.StateFile);
            _status.Text = _active ? "已启用 · 关闭工具箱时自动恢复" : "启用失败，电源设置未保持修改";
            _status.ForeColor = _active ? Color.FromArgb(25, 135, 78) : Color.FromArgb(190, 72, 72);
            UpdateUi();
            RefreshSnapshot();
        }

        private bool RunRecoveryElevated()
        {
            ProcessStartInfo info = new ProcessStartInfo(GuardPaths.InstalledExe, "recover") { UseShellExecute = true, Verb = "runas" };
            try
            {
                Process process = Process.Start(info);
                process.WaitForExit(10000);
                _active = File.Exists(GuardPaths.StateFile);
                _status.Text = _active ? "恢复失败，请重试" : "已恢复为启用前的设置";
                _status.ForeColor = _active ? Color.FromArgb(190, 72, 72) : LidTheme.Muted;
                UpdateUi();
                RefreshSnapshot();
                return !_active;
            }
            catch { return false; }
        }

        private void RefreshSnapshot()
        {
            try
            {
                PowerPlanSnapshot snapshot = PowerPlanService.ReadCurrent();
                _scheme.Text = "计划 GUID  ·  " + snapshot.SchemeGuid.ToString();
                _details.Text = string.Format("合盖动作   插电 {0}  ·  电池 {1}      空闲睡眠   插电 {2}  ·  电池 {3}", ActionName(snapshot.LidAc), ActionName(snapshot.LidDc), SleepName(snapshot.SleepAc), SleepName(snapshot.SleepDc));
            }
            catch (Exception ex)
            {
                _scheme.Text = "无法读取当前电源计划";
                _details.Text = ex.Message;
            }
        }

        private void UpdateUi()
        {
            _active = IsActive;
            _enable.Enabled = !_active;
            _restore.Enabled = _active;
            _ac.Enabled = _dc.Enabled = _idle.Enabled = !_active;
        }

        private static void AddOptionRow(Panel card, ToggleSwitch toggle, int top, string title, string description, bool isChecked)
        {
            Label titleLabel = MakeLabel(title, 24, top, 470, 24, 9.5F, FontStyle.Bold, LidTheme.Text);
            Label detailLabel = MakeLabel(description, 24, top + 23, 470, 21, 8.3F, FontStyle.Regular, LidTheme.Muted);
            toggle.Checked = isChecked;
            toggle.AccessibleName = title;
            toggle.Top = top + 8;
            toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            toggle.Left = card.Width - 76;
            card.Resize += delegate { toggle.Left = card.ClientSize.Width - 76; };
            titleLabel.Cursor = Cursors.Hand;
            detailLabel.Cursor = Cursors.Hand;
            titleLabel.Click += delegate { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
            detailLabel.Click += delegate { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
            card.Controls.AddRange(new Control[] { titleLabel, detailLabel, toggle });
        }

        private static Label MakeLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        {
            Label label = new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft };
            label.SetBounds(x, y, w, h);
            return label;
        }

        private static string ActionName(uint value) { return value == 0 ? "不操作" : value == 1 ? "睡眠" : value == 2 ? "休眠" : value == 3 ? "关机" : value.ToString(); }
        private static string SleepName(uint value)
        {
            if (value == 0) return "从不";
            TimeSpan duration = TimeSpan.FromSeconds(value);
            return duration.TotalHours >= 1 ? string.Format("{0:0.#} 小时", duration.TotalHours) : string.Format("{0:0} 分钟", duration.TotalMinutes);
        }
    }
}
