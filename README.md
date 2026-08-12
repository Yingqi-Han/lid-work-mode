# Lid Work Mode / 合盖继续运行

Yingqi Tools 的可独立维护组件。它在用户明确启用的当次会话中，临时将 Windows 当前电源计划的合盖动作改为 `Do nothing`，并在工具箱退出、崩溃、电源计划切换或下次开机时恢复原值。

## Safety model

- 默认只处理 AC（插电），DC（电池）需要用户主动勾选。
- 禁止空闲睡眠是独立选项，默认关闭。
- 应用设置需要 UAC，主程序不以管理员身份运行。
- PowerGuard 不接受任意 GUID、数值或命令。
- 合盖持续运行会带来耗电和积热，请勿放入背包、被褥或不通风环境。

## Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

需要 .NET 10 SDK（仓库用 `global.json` 固定为 10.0.101），也可以通过 `YINGQI_DOTNET` 指定 SDK 路径。发布的 `PowerGuard.exe` 是 `win-x64` self-contained 单文件，不要求用户预装运行库。

## Public API

- `PowerPlanService.ReadCurrent()`
- `EnableOptions { Ac, Dc, PreventIdleSleep }`
- `LidWorkModeControl`
- `PowerGuard.exe install|enable|recover|status|self-test`

`LidWorkModeControl` 已迁移为 WPF Fluent `UserControl`；PowerGuard 仍是独立、无 UI 依赖的安全进程，恢复日志 schema 和固定命令保持兼容。

MIT licensed. No telemetry or network access.
