using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace LidWorkMode;

public partial class LidWorkModeControl : UserControl
{
    private Process? _guard;
    private bool _active;
    private bool _suppressBatteryDialog;

    public bool IsActive => _active || File.Exists(GuardPaths.StateFile);

    public LidWorkModeControl()
    {
        InitializeComponent();
        RefreshSnapshot();
        UpdateUi();
    }

    public async Task<bool> RestoreAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsActive) return true;
        SetBusy(true);
        try
        {
            try
            {
                using EventWaitHandle stop = EventWaitHandle.OpenExisting(GuardPaths.StopEventName);
                stop.Set();
                DateTime deadline = DateTime.UtcNow.Add(timeout);
                while (File.Exists(GuardPaths.StateFile) && DateTime.UtcNow < deadline)
                    await Task.Delay(100, cancellationToken);
                _active = File.Exists(GuardPaths.StateFile);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _active = !await RunRecoveryElevatedAsync(timeout, cancellationToken);
            }
            SetStatus(_active ? InfoBarSeverity.Warning : InfoBarSeverity.Success, _active ? "恢复尚未完成" : "已恢复原设置", _active ? "请重试，或在下次启动时由 PowerGuard 恢复。" : "所有托管值已写回启用前状态。");
            return !_active;
        }
        finally { UpdateUi(); RefreshSnapshot(); SetBusy(false); }
    }

    public bool RestoreAndWait(int timeoutMilliseconds) => RestoreAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).GetAwaiter().GetResult();

    public bool ScrollByWheelDelta(int delta)
    {
        if (ContentScroller.ScrollableHeight <= 0) return false;
        double previousOffset = ContentScroller.VerticalOffset;
        ContentScroller.ScrollToVerticalOffset(previousOffset - delta);
        return !ContentScroller.VerticalOffset.Equals(previousOffset);
    }

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (AcToggle.IsChecked != true && DcToggle.IsChecked != true)
        {
            SetStatus(InfoBarSeverity.Warning, "请选择启用范围", "至少选择插电或电池中的一种状态。");
            return;
        }
        if (!File.Exists(GuardPaths.InstalledExe))
        {
            SetStatus(InfoBarSeverity.Error, "缺少 PowerGuard", "请重新运行 Yingqi Tools 安装程序。");
            return;
        }
        SetBusy(true);
        try
        {
            var info = new ProcessStartInfo(GuardPaths.InstalledExe, $"enable {(AcToggle.IsChecked == true ? 1 : 0)} {(DcToggle.IsChecked == true ? 1 : 0)} {(IdleToggle.IsChecked == true ? 1 : 0)} {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" };
            try { _guard = Process.Start(info); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                SetStatus(InfoBarSeverity.Informational, "已取消", "没有修改任何电源设置。");
                return;
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(GuardPaths.StateFile) && _guard is { HasExited: false } && DateTime.UtcNow < deadline) await Task.Delay(100);
            _active = File.Exists(GuardPaths.StateFile);
            SetStatus(_active ? InfoBarSeverity.Success : InfoBarSeverity.Error, _active ? "已启用" : "启用失败", _active ? "关闭 Yingqi Tools 时会自动恢复原设置。" : "电源设置未保持修改。");
        }
        finally { UpdateUi(); RefreshSnapshot(); SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreAsync(TimeSpan.FromSeconds(10));

    private async void DcToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressBatteryDialog) return;
        var dialog = new ContentDialog
        {
            Title = "确认使用电池模式",
            Content = new System.Windows.Controls.TextBlock { Text = "电池模式会明显增加耗电和合盖积热。请勿将运行中的电脑放入背包、被褥或不通风环境。", TextWrapping = TextWrapping.Wrap, Width = 420 },
            PrimaryButtonText = "我了解风险",
            CloseButtonText = "取消",
            PrimaryButtonAppearance = ControlAppearance.Caution,
            DialogHostEx = DialogHost
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            _suppressBatteryDialog = true;
            DcToggle.IsChecked = false;
            _suppressBatteryDialog = false;
        }
    }

    private async Task<bool> RunRecoveryElevatedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(GuardPaths.InstalledExe, "recover") { UseShellExecute = true, Verb = "runas" })!;
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            return !File.Exists(GuardPaths.StateFile);
        }
        catch { return false; }
    }

    private void RefreshSnapshot()
    {
        try
        {
            PowerPlanSnapshot snapshot = PowerPlanService.ReadCurrent();
            PowerSummaryText.Text = $"合盖：插电 {ActionName(snapshot.LidAc)} · 电池 {ActionName(snapshot.LidDc)}    空闲睡眠：插电 {SleepName(snapshot.SleepAc)} · 电池 {SleepName(snapshot.SleepDc)}";
            SchemeText.Text = $"当前计划 GUID：{snapshot.SchemeGuid}";
        }
        catch (Exception ex) { PowerSummaryText.Text = "无法读取当前电源计划"; SchemeText.Text = ex.Message; }
    }

    private void UpdateUi()
    {
        _active = IsActive;
        EnableButton.IsEnabled = !_active;
        RestoreButton.IsEnabled = _active;
        AcToggle.IsEnabled = DcToggle.IsEnabled = IdleToggle.IsEnabled = !_active;
        StateBadge.Content = _active ? "已启用" : "待命";
        StateBadge.Appearance = _active ? ControlAppearance.Success : ControlAppearance.Secondary;
    }

    private void SetBusy(bool busy)
    {
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { EnableButton.IsEnabled = false; RestoreButton.IsEnabled = false; }
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity; StatusBar.Title = title; StatusBar.Message = message;
    }

    private static string ActionName(uint value) => value switch { 0 => "不操作", 1 => "睡眠", 2 => "休眠", 3 => "关机", _ => value.ToString() };
    private static string SleepName(uint value)
    {
        if (value == 0) return "从不";
        TimeSpan duration = TimeSpan.FromSeconds(value);
        return duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#} 小时" : $"{duration.TotalMinutes:0} 分钟";
    }
}
