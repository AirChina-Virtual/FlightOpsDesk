# R4：应用层会话、交互闸门与用例服务

先读 [简报规则与通用约束](README.md)，它们全部适用于本任务。

## 目标

把 `MainWindow` 里的状态管理、保存策略和业务判断移到 `VamSys.Application`，让界面只负责展示和转发操作。行为、文案、保存时机和保存内容都保持不变。完成后 `MainWindow` 不再直接调用存储，也不含业务判断。这是 R6 重写界面的前提。

## 工作树与分支

- 工作树：`F:\vCCA\wt-r4-services`
- 分支：`r4/services`，起点 `7750c70`
- 并行任务：`F:\vCCA\wt-r5-adapter` 负责 `src/VamSys.Infrastructure`（`Transport/` 之外的 `Operations*.cs`、`CandidateIndex.cs`、`DemoService.cs`、`ApiContracts.cs`）。本任务不得修改这些文件，只能调用它们现有的公开或内部接口。需要 Infrastructure 新增接口时，先在 Application 定义端口接口，再在 App 中用现有类适配，并在结果中注明。

## 负责的文件

- `src/VamSys.Application/`：新增 `WorkspaceSession`、`InteractionGate`、各用例服务和 `DemoRunner`；现有的 `DraftEdits`、`BatchExecutor`、`WindowCloseController` 可以按需整理。
- `src/VamSys.App/MainWindow*.cs`、`TaskSubmission.cs`、`UnknownReview.cs`、`BatchEditPreview.cs`：改为调用上述服务。
- `tests/VamSys.UnitTests/Application/`：新测试。
- `tests/UiVerification/MainWindow.Verification.cs`：只做随调用点变化必需的修改，QA 命令和 AutomationId 不变。
- `docs/TEST-COVERAGE.md`、`src/VamSys.Application/README.md`、`src/VamSys.App/README.md`。

## 必读

- [重构计划](../REFACTOR-PLAN-2026-10-20.md)：4.2 节中 `WorkspaceSession`、`InteractionGate`、用例服务三行，以及“R4 应用层”。
- `src/VamSys.App/README.md` 中“修改代码时要保留的行为”：保存、数据页、审阅、任务、提交交接、语言。
- `src/VamSys.Storage/README.md`：变更日志与各种保存入口（`SaveChanges`／`SaveHeader`／`SaveJobs`、定向查询）。
- 源码：`src/VamSys.App/MainWindow.xaml.cs`（`Save`、`SaveEdits`、`Work`、`Guard`、关闭流程、各标志位），再按需读对应的 `MainWindow.*.cs`。

## 做法与约束

1. **WorkspaceSession**：持有当前工作区、存储、保存锁、`DraftEdits` 和自动保存节奏，对外提供 `SaveAsync`／`SaveEditsAsync`／`SaveHeaderAsync`／`SaveJobsAsync` 和切换工作区。`MainWindow` 中的 `store`、`saveGate`、`edits` 移入其中。保存失败恢复待保存编辑、`storageBlocked` 的语义和“改后立即保存”的时机都不变。
2. **InteractionGate**：用一个状态对象替代 `busy`、`localOperation`、`querying`、`interactiveOperations`、`dialogs`、`closeState.IsOpen`、`storageBlocked` 的分散判断，提供 `CanEdit`、`CanNavigate`、`CanSubmit`、`CanClose` 这类查询和进入／退出操作的作用域。保持现有的每一个组合行为，包括关闭被阻止时的提示、本地操作结束时由后台任务负责解锁的规则；用表格测试穷举状态组合。
3. **用例服务**：每次抽一个服务、提交一次，顺序依次是：诊断导出 → 维护（参数读取、备份清理）→ 导入导出 → 批量编辑与机型分配 → 连接与刷新 → 审阅（`ReviewPlanBuilder` 已不依赖 WinUI，服务包装它）→ 提交交接 → 任务恢复（继续、放弃未发出、核对未知、接受服务器当前值、删除只读核对）。服务只依赖 Application、Domain 的类型和端口接口；对话框和文件选择仍由界面提供，通过委托或接口传入。
4. **DemoRunner**：把演示任务的执行和快照回填从界面代码移出，行为不变（演示是否改用在线流水线属于决定 R-D6，本任务不做）。
5. 每个服务至少有覆盖正常路径、失败路径和保存失败不改内存的单元测试。被新测试完全覆盖的旧场景（例如 `TaskSubmissionScenarios`、`UiInteractionScenarios`、`TaskViewModelScenarios` 中的部分）可以按规则删除。
6. 不改界面布局和 XAML（那是 R6），不改文案和资源键。

本任务允许的清单变化：AutomationId 清单只允许因文件位置移动而产生的路径变化，id 本身不变；其他清单不变。

## 完成标准

- `pwsh scripts/check-all.ps1 -Mode Full` 以 `ALL PASSED` 结束；QA 包能构建：`dotnet publish src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:UiVerification=true -p:VamSysTestSupport=true -p:RestoreLockedMode=true -o artifacts\qa`。
- `MainWindow*.cs` 不再引用 `WorkspaceStore`，也不再出现上述 7 个标志位的直接判断；在结果中给出 `MainWindow*.cs` 改动前后的总行数。
- `ArchitectureBoundaryTests` 增加一条：Application 不引用 WinUI、SQLite 或 HttpClient。
- `docs/TEST-COVERAGE.md` 中 F14、F16、F20、F21、F24 等应用层行更新。

## 结果（由子代理填写）

不超过 20 行：做了什么、提交列表、检查结果、关键数字、未完成或有风险的地方。
