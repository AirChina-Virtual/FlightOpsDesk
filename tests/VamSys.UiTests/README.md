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
- 全部运行约 3 到 5 分钟。截图和测量结果（布局组合截图、`summary.json`、`task-ui-benchmark.json`）留在测试的输出目录，供人工查看。

## 驱动

| 文件 | 内容 |
|---|---|
| `Driver/AppWindow.cs` | 启动、重启与关闭、QA 管道命令、等待、按 AutomationId 或名称找元素和按钮、前台、真实鼠标与按键 |
| `Driver/Disk.cs` | 读取关闭后留在磁盘上的工作区 |
| `Driver/Patterns.cs` | 调用、读写文本、只读状态、展开／收起、选择、勾选；QA 管道应答的取值 |
| `Driver/WindowFactAttribute.cs` | 显式窗口测试 |

## 新增的流程

以下流程原来没有窗口测试：

| 测试类 | 内容 | 包 |
|---|---|---|
| `ConflictReviewTests` | 审阅页处理冲突：保留本地、采用远端、中途停下、服务器已删除的记录 | 正式包 |
| `CsvImportTests` | 真实系统文件选择框、列映射、未知列、只生成草稿 | 正式包 |
| `SubmissionHandoffTests` | 演示提交：两步确认可取消、切到任务中心、只运行本次任务 | 正式包 |
| `DialogTests` | 其他测试没打开过的文字、列表对话框：新建工作区、校验结果、审阅问题、删除被阻止、批量规则与到期预览、机队校验失败、导出（含真实文件夹选择框）、放弃未发送项、备份并清空、离线时的未知结果 | 正式包 |
| `ParameterPickerTests` | 单元格按钮的两种拒绝；选择框用 QA 包虚构的参数，因为真实列表需要服务器 | QA 包 |

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

## 写测试时要注意

- 产品字符串用 `WaitText`、`Click` 等，AutomationId 清单会核对；测试自己的数据或运行时拼出的文字用 `WaitData`，或把名称放进变量，不进清单。
- 对话框的按钮按下后，程序内部的操作还要过一会儿才结束，这期间的点击和关闭会被程序拒绝。用 `ClickUntilFilePicker` 这样重复点击，或在 QA 包里等 `state` 的 `idle`。
- 工作区打开时总是离线（`WorkspaceSession` 把存储的在线模式重置），只有连上服务器才切回在线。种子写在线模式也没用，在线校验、人工确认删除、核实未生效以及显示服务器读取结果的恢复对话框，窗口测试都打不开。
- 系统文件选择框里 id `1`、`2` 不止一个元素在用（文件列表项也有），`1` 有时还是按钮外面的容器，要同时看控件类型。
