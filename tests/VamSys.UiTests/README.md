# 窗口测试（VamSys.UiTests）

用 C# 和 Windows 自带的 UI 自动化客户端（`UIAutomationClient`，见计划 R-D9）驱动 QA 包或正式包。原来的 PowerShell 窗口脚本已全部改写并删除。

每个测试都标为显式（`[WindowFact]`）：会启动程序并占用桌面焦点、键盘和鼠标，所以 `check-all` 和普通 `dotnet test` 只编译、不运行，结果显示为跳过。运行前先构建两个包，并关闭所有 FlightOps Desk 窗口：

~~~powershell
./scripts/build.ps1 -Publish
./scripts/build.ps1 -Publish -UiVerification
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only --filter-not-trait Category=Measurement
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only --filter-class VamSys.UiTests.DataFooterTests
$env:VAMSYS_BASELINE_LAUNCHES = '1'  # 基线测量的启动次数，默认 5
dotnet test --project tests/VamSys.UiTests/VamSys.UiTests.csproj -c Release -- --explicit only --filter-trait Category=Measurement
~~~

- 每个测试在 artifacts/ui-tests/<名称>/data-<GUID> 新建数据目录，在进程内准备数据（与 `tools/VamSys.QaSeed` 的种子相同），不使用真实数据、不调用真实 API。
- 每个测试选用的包写在 `AppWindow.Start` 的参数里，默认 QA 包（artifacts/v3-qa，`VAMSYS_QA_EXE` 可指定另一个）；要检查正式构建的用正式包（artifacts/flightops-app，`VAMSYS_APP_EXE` 可指定另一个，没有 QA 管道）。
- 测试只结束自己启动的进程；已有 FlightOps Desk 窗口时直接失败。
- 键盘和鼠标输入前会把窗口切到前台，切不过去就失败，避免输入落到别的窗口。
- 普通用例运行约 4 分钟；基线测量（`Category=Measurement`，只测量、不设门槛）要另外运行。截图和测量结果（布局组合截图、`summary.json`、`benchmark.json`、`baseline.json` 等）留在测试的输出目录，供人工查看。

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
| `DialogTests` | 其他测试没打开过的文字、列表对话框：新建工作区、校验结果、审阅问题、删除被阻止、批量规则与到期预览（到期的结束日期、平移天数的无效输入和跳过行）、机队校验失败、导出（含真实文件夹选择框）、放弃未发送项、备份并清空、离线时的未知结果 | 正式包 |
| `ParameterPickerTests` | 单元格按钮的两种拒绝；选择框用 QA 包虚构的参数，因为真实列表需要服务器 | QA 包 |
| `ConnectedDialogTests` | 只在连接后出现的对话框（4.1）：在线校验、人工确认删除、无回执删除的三种结果（已删除、与修改前一致、被他人修改）、核实未生效、接受服务器值、无法接受服务器值；还有自动找回 ID 的多条候选（1.4）、审阅页和任务中心移除未获准机队（2.3、2.4）、两项无回执删除一次确认（1.6）；以及在窗口中经审阅页、确认框和任务中心真正提交一次（`Dependents_of_a_create_settled_at_the_end_of_the_round_run_in_that_round`）：模拟服务器建了新机队但应答里读不出 ID，本轮结束核对找回 ID 后，依赖它的飞机和航线在同一轮执行，任务一次完成并核对。QA 包通过 `VAMSYS_QA_OPERATIONS_ORIGIN` 连接测试进程内的模拟服务器（`LoopbackOperationsServer`），其余场景的任务由 `ConnectedUiFixture` 在进程内用真实执行器对同一模拟服务器跑出来，测试断言之后只有 GET；同轮执行的场景只预置草稿，提交在窗口里发生，断言三条记录各 POST 一次、其余只有 GET | QA 包 |
| `DataLocationTests` | 更改数据目录：真实文件夹选择框、复制、重启、旧库改名、切回默认位置；位置缺失时的恢复窗口；环境变量锁定。默认目录用只存在于 QA 包的 `VAMSYS_QA_DEFAULT_DIR` 重定向，不碰真实的 `%LOCALAPPDATA%` | QA 包 |

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
| `DataPerformanceTests` | `test-data-performance-ui.ps1`（`-ScreenshotsOnly` 去掉，完整运行就有这些截图） |
| `LargeTableLanguageTests` | `test-api-ui-20k.ps1` |
| `BaselineMeasurements` | `test-baseline-ui.ps1` |

## 写测试时要注意

- 产品字符串用 `WaitText`、`Click` 等，AutomationId 清单会核对；测试自己的数据或运行时拼出的文字用 `WaitData`，或把名称放进变量，不进清单。
- 对话框的按钮按下后，程序内部的操作还要过一会儿才结束，这期间的点击和关闭会被程序拒绝。用 `ClickUntilFilePicker` 这样重复点击，或在 QA 包里等 `state` 的 `idle`。
- 工作区打开时总是离线（`WorkspaceSession` 把存储的在线模式重置），只有连上服务器才切回在线。需要连接的对话框用 `StartConnected`：它把种子返回的模拟服务器挂到本机回环地址，再让 QA 包连过去；测试先在设置页点“测试 Operations 连接”。
- 系统文件选择框里 id `1`、`2` 不止一个元素在用（文件列表项也有），`1` 有时还是按钮外面的容器，要同时看控件类型。
