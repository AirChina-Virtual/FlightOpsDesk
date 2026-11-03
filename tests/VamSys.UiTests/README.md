# 窗口测试（VamSys.UiTests）

用 C# 和 Windows 自带的 UI 自动化客户端（`UIAutomationClient`，见计划 R-D9）驱动 QA 包，逐个替换 ../../scripts 下的 PowerShell 窗口脚本。

每个测试都标为显式（`[WindowFact]`）：会启动 QA 包并占用桌面焦点和键盘，所以 `check-all` 和普通 `dotnet test` 只编译、不运行，结果显示为跳过。运行前先构建 QA 包，并关闭所有 FlightOps Desk 窗口：

~~~powershell
./scripts/build.ps1 -Publish -UiVerification
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only --filter-class VamSys.UiTests.DataFooterTests
~~~

- 每个测试在 artifacts/ui-tests/<名称>/data-<GUID> 新建数据目录，在进程内准备数据（与 `tools/VamSys.QaSeed` 的种子相同），不使用真实数据、不调用真实 API。
- `VAMSYS_QA_EXE` 可指定另一个 QA 包；默认是 artifacts/v3-qa/FlightOpsDesk.exe。
- 测试只结束自己启动的进程；已有 FlightOps Desk 窗口时直接失败。
- 键盘输入前会把窗口切到前台，切不过去就失败，避免按键落到别的窗口。

## 驱动

| 文件 | 内容 |
|---|---|
| `Driver/QaWindow.cs` | 启动与关闭、QA 管道命令、等待、按 AutomationId 或名称找元素、前台与按键 |
| `Driver/Patterns.cs` | 调用、读写文本、展开／收起、选择；QA 管道应答的取值 |
| `Driver/WindowFactAttribute.cs` | 显式窗口测试 |

## 已替换的脚本

| 测试类 | 原脚本 |
|---|---|
| `DataFooterTests` | `test-data-footer-ui.ps1` |
