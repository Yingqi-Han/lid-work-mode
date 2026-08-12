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

## Public API

- `PowerPlanService.ReadCurrent()`
- `EnableOptions { Ac, Dc, PreventIdleSleep }`
- `LidWorkModeControl`
- `PowerGuard.exe install|enable|recover|status|self-test`

MIT licensed. No telemetry or network access.
