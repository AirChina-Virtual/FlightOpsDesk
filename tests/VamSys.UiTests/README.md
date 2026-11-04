# 窗口测试（VamSys.UiTests）

用 C# 和 Windows 自带的 UI 自动化客户端（`UIAutomationClient`，见计划 R-D9）驱动 QA 包或正式包，逐个替换 ../../scripts 下的 PowerShell 窗口脚本。

每个测试都标为显式（`[WindowFact]`）：会启动程序并占用桌面焦点、键盘和鼠标，所以 `check-all` 和普通 `dotnet test` 只编译、不运行，结果显示为跳过。运行前先构建两个包，并关闭所有 FlightOps Desk 窗口：

~~~powershell
./scripts/build.ps1 -Publish
./scripts/build.ps1 -Publish -UiVerification
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only --filter-class VamSys.UiTests.DataFooterTests
~~~

- 每个测试在 artifacts/ui-tests/<名称>/data-<GUID> 新建数据目录，在进程内准备数据（与 `tools/VamSys.QaSeed` 的种子相同），不使用真实数据、不调用真实 API。
- 每个测试选用的包写在 `AppWindow.Start` 的参数里，默认 QA 包（artifacts/v3-qa，`VAMSYS_QA_EXE` 可指定另一个）；要检查正式构建的用正式包（artifacts/flightops-app，没有 QA 管道）。
- 测试只结束自己启动的进程；已有 FlightOps Desk 窗口时直接失败。
- 键盘和鼠标输入前会把窗口切到前台，切不过去就失败，避免输入落到别的窗口。
- 全部运行约 3 分钟。截图和测量结果（布局组合截图、`summary.json`、`task-ui-benchmark.json`）留在测试的输出目录，供人工查看。

## 驱动

| 文件 | 内容 |
|---|---|
| `Driver/AppWindow.cs` | 启动、重启与关闭、QA 管道命令、等待、按 AutomationId 或名称找元素和按钮、前台、真实鼠标与按键 |
| `Driver/Patterns.cs` | 调用、读写文本、只读状态、展开／收起、选择、勾选；QA 管道应答的取值 |
| `Driver/WindowFactAttribute.cs` | 显式窗口测试 |

## 已替换的脚本

| 测试类 | 原脚本 |
|---|---|
| `DataFooterTests` | `test-data-footer-ui.ps1` |
| `CellEditingTests` | `test-cell-editing-ui.ps1` |
| `ConnectionSettingsTests` | `test-credentials-ui.ps1`、`test-settings-save-ui.ps1` |
| `StorageStartupTests` | `test-storage-ui.ps1` |
| `RecoveryTests` | `test-recovery-ui.ps1` |
| `ClosingAndHistoryTests` | `test-v3-verification-ui.ps1`（默认、`-Autosave`、`-History`） |
| `TaskPageTests` | `test-v3-verification-ui.ps1 -Tasks`、`-InteractionsOnly` |
| `LocalizationTests` | `test-localization-ui.ps1` |
| `DataInteractionTests` | `test-ui-interactions.ps1`（2 万行、5 万行、`-ReviewOnly`） |
