# 构建与工具链验证：VS 2026 环境（2026-11-14）

- 目的：本机由 VS2022 切换为 **Visual Studio Community 2026** 后，重新构建 DSHRemote 全链路，排查并修复构建工具链问题。
- 结论：**构建链已全绿**；仓库配置（`global.json`/`NuGet.config`/csproj）**无需修改**。唯一遗留问题是 `dotnet test` 的 VSTest testhost 在受限/父进程托管环境下无法启动（非仓库缺陷），已提供脚本化替代方案并通过全部测试。

## 1. 环境

| 项 | 值 |
| --- | --- |
| Visual Studio | Community 2026 `18.12.12405.243`（`C:\Program Files\Microsoft Visual Studio\18\Community`） |
| .NET SDK（`dotnet --version`） | `10.0.114`（`global.json` 固定 `10.0.114` + `rollForward: latestPatch`，无需改动） |
| 宿主运行时 | `11.0.0`（x64） |
| Android | `scripts/build-apk.ps1` 默认 `D:\SmartBillAndroidSdk`（API 36）+ `C:\Program Files\Java\jdk-21` |
| 依赖仓 | 兄弟 checkout `D:\Code\VibeCoding\Arc.Avalonia`（0.9.0-alpha 源码项目引用）、`D:\Code\VibeCoding\PuraMarkdown`（本地 NuGet feed） |

## 2. 构建结果（全部成功）

| 目标 | 命令 | 结果 |
| --- | --- | --- |
| **整个解决方案** | `dotnet build app/DshRemote.sln -c Debug` | ✅ 0 warning / 0 error（含 Core/App/Desktop/Android + Arc.Avalonia 项目引用） |
| Core 库 | `dotnet build app/src/DshRemote.Core -c Debug` | ✅ 0 warning / 0 error |
| App 库（含 Arc.Avalonia 项目引用） | `dotnet build app/src/DshRemote.App -c Debug` | ✅ 0 warning / 0 error |
| Desktop 头 | `dotnet build app/src/DshRemote.App.Desktop -c Debug` | ✅ 0 warning / 0 error |
| App 测试工程 | `dotnet build app/tests/DshRemote.App.Tests -c Debug` | ✅ 0 warning / 0 error |
| Android Debug APK | `pwsh -File scripts/build-apk.ps1 -Configuration Debug` | ✅ 0 warning / 0 error；产物 `app/src/DshRemote.App.Android/bin/Debug/net10.0-android/dev.dshremote.app-Signed.apk`（83,354,629 bytes，约 79.5 MB） |

## 3. 工具链问题与修复

### 3.1 VS2022 时代的嵌套构建失败（已消除）

VS2022 环境下 `dotnet build app/tests/DshRemote.App.Tests` 会在 `_GetProjectReferenceTargetFrameworkProperties`
阶段静默失败（外层“生成失败 0 error”，诊断日志里是
`MSB4276: 默认 SDK 解析程序解析 SDK "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator" 失败`
经 Arc.Avalonia 的严格构建配置升级为错误）。这是**环境/SDK 安装缺件**，不是仓库代码问题。

VS2026 安装完成、并等待兄弟仓 Arc.Avalonia 的工具链修复落地后，同一条命令 **0 warning / 0 error**；
`global.json`、`NuGet.config`、`Directory.Build.props/targets` 均未改动。

### 3.2 `dotnet test`（VSTest）testhost 无法启动（环境限制，已绕过）

现象（Core.Tests / App.Tests 均复现）：

```
Testhost process ... exited with error: Unhandled exception.
System.ComponentModel.Win32Exception (5): 拒绝访问。
  at System.Diagnostics.ProcessManager.OpenProcess(...)
  at Microsoft.VisualStudio.TestPlatform.TestHost.DefaultEngineInvoker.SetParentProcessExitCallback(...)
Test Run Aborted.
```

- 原因：VSTest 的 `testhost` 启动时会打开父进程句柄以监听 runner 退出；在当前“父进程托管/受限”的 shell 环境下 `OpenProcess` 被拒绝，测试尚未开始即 abort。
- 已排除：`-a x64`（换 x64 testhost）、放开文件沙箱（`danger-full-access`）均不能绕过；与仓库代码/配置无关，普通终端里运行 `dotnet test` 不受影响。
- 处理：仓库新增 **脚本化替代运行器**（不进入 solution，按需构建）：
  - `scripts/test-fallback/DshTestFallback.csproj` + `Program.cs`：用 `xunit.runner.utility` 在进程内执行 xUnit v2 测试，打印 `passed/failed/skipped`；
  - `scripts/run-tests.ps1`：构建两个测试工程 → 把 fallback runner 复制到测试输出目录（保证依赖解析与 testhost 一致）→ 运行并汇总结果；支持 `-Filter`、`-Project`、`-Configuration`，`-PreferDotnetTest` 时先尝试标准 `dotnet test`（普通终端可用）；
  - `scripts/e2e.ps1` 的单测步骤已接入：检测到 `Test Run Aborted` 时自动改用 `run-tests.ps1 -Project DshRemote.Core.Tests`，本机验证 `[PASS] core-tests 182 test(s) passed`。

验证结果（本机 VS2026 环境，`pwsh -File scripts/run-tests.ps1`）：

```
DshRemote.Core.Tests   passed=182 failed=0 skipped=0
DshRemote.App.Tests    passed=86  failed=0 skipped=0
[+] All test projects passed.
```

其中包含本次「压缩会话支持」新增的解析/时间线/缓存回归用例（见
[`session-compaction-support.md`](session-compaction-support.md) §12）。

## 4. 模拟器验证（Android 16，emulator-5556，2026-11-14）

`pwsh -File scripts/e2e.ps1 -Serial emulator-5556` → **8/8 PASS**（relay-smoke / core-tests 182 / smoke-tool 371 会话 /
Debug APK 79.2 MB / adb / 安装 / pairing-push / app-launch）。详见
[`docs/evidence/20261114-compaction-support/emulator/README.md`](evidence/20261114-compaction-support/emulator/README.md)。

压缩功能在模拟器上的端到端验证：

1. **真实手动压缩**（宿主 `commands/execute` 执行 `/compact`，App 实时跟随）：压缩卡按检查点锚点出现，
   折叠态显示「手动压缩 · 已压缩 4 项 · 约 2.2k tokens」，展开后渲染摘要 Markdown；
   trace：`surfaceReplaced=1 suppressed=0 :: User#8 | TURN#1 | Compaction#23`。
2. **原 14:18 错位回归**（`session-127b430a`，加载更早历史把 38 组 prune/replace 读入窗口）：
   trace：`surfaceReplaced=38 pruned=38 suppressed=0 :: TURN#5 | User#1955 | TURN#6 | User#2658 | TURN#7`；
   turn 6 最终答案/「本轮文件」之后无裸「工具 · 结果」卡片。

模拟器已知限制（非本次缺陷）：聊天页部分中文缺字形（模拟器字体）、11k 事件巨型历史会话快速连续点「加载更早」
会触发 ANR（既有长会话性能范畴，debug/模拟器）。

## 5. 与压缩会话支持的关系

本次代码改动（Core surface 解析、App 投影闸门/状态机、压缩卡片 UI、缓存/指纹重建）**已在本环境完成编译与全部单测验证**；
真实日志回放 POC 见 [`evidence/20261114-compaction-support/`](evidence/20261114-compaction-support/README.md)。

尚未做（需设备/长会话）：

- 真机（或模拟器）上打开发生过压缩的长会话，确认「上下文压缩」卡片与历史顺序；
- `scripts/e2e.ps1` 全链路（relay smoke → APK 安装 → 截图）——如遇 `dotnet test` 同款 testhost 限制，可先用 `run-tests.ps1` 跑单测，再单独执行 APK/设备步骤。

## 5. 改动文件

| 文件 | 说明 |
| --- | --- |
| `scripts/run-tests.ps1` | 测试运行入口；标准 `dotnet test` 不可用时自动走进程内 runner |
| `scripts/test-fallback/DshTestFallback.csproj`、`Program.cs` | xUnit v2 进程内 runner（`xunit.runner.utility`） |
| `docs/session-compaction-support.md` | 压缩会话支持设计与实施状态（本轮代码改动 + 验证结果） |
| `docs/evidence/20261114-compaction-support/` | 语义/代码审查、真实日志回放 POC、压缩事件盘点 |
