# C4：界面模型、审阅与提交交接旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c4-app-models-legacy`
- 分支：`cleanup/c4-app-models-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`UiInteractionScenarios.cs`、`TaskViewModelScenarios.cs`、`TaskSubmissionScenarios.cs`、`DataTableScenarios.cs`
- 新测试目录：`tests/VamSys.UnitTests/Application/`、`tests/VamSys.UnitTests/Review/`、`tests/VamSys.UnitTests/Presentation/`（新建，放任务页模型与数据页模型测试）

## 说明

**保留：** `DataUiFixture`（`--seed-data-ui` 用）。`TaskViewModels.cs`、`DataTableViewModel.cs`、`ViewModels.cs` 仍在 App 项目中，以链接源文件方式编进测试；新测试可以用同样方式链接到 `VamSys.UnitTests`（参考它已链接 `BatchEditPreview.cs` 的写法）。
已有可参考的新测试：`UnitTests/Application/SubmissionServiceTests.cs`、`ReviewServiceTests.cs`、`UnitTests/Review/*`。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

删除前／后旧运行器场景数：232 → 201（删除 31 个：界面 12、任务页模型 3、提交交接 9、数据页 7；Full 档 ALL PASSED，单元 1419／集成 195）
已删除场景（新测试均在 VamSys.UnitTests 下）：
- 审阅、样本、依赖、内容键：Review.ReviewPlanBehaviorTests.{Plan_matches_the_planner…, Preview_errors_block_only_their_entry…, A_dependency_on_a_deferred_create_defers_transitively, Verified_operations_lift_the_sample_limit…, Not_connected_waits_for_connection…, Reference_fields_are_resolved…}、Review.ReviewContentKeyTests（全部）；缺口新增 Review.ReviewPlanGuardTests（4 项：两种全局阻断、相邻值／字段名防碰撞、键缓存与 SHA-256 长度、真实适配器无请求无令牌）
- 批量范围、删除、无变化、恢复、停用日期：Application.BatchEditServiceTests、Domain.DeletionServiceTests、Domain.RuleEngineTests；新增 Application.BatchSelectionTests（4 项，对应原 4 个场景逐条断言）
- 密度偏好：新增 Application.TableDensityStoreTests（真实 WorkspaceStore）
- 提交交接 8 项：Application.SubmissionServiceTests（顺序、取消／空、保存失败、提交后失败、上下文变化、启动校验）；缺口新增 Application.SubmissionHandoffTests（5 项：解锁前选页、慢审阅重新加锁被拒且不重试、锁／忙碌／查询／关闭变化、被拒的本地操作、两种语言的启动提示）；Unknown 审阅新增 Application.UnknownReviewTests（2 项）
- 过期版本导致保存失败：IntegrationTests.Storage.JournalWriteScopeTests（StorageStale）
- 实际在线提交 3 条航线 DELETE：未做成单个端到端测试，分别由 IntegrationTests.Api.WriteReadbackAndRecoveryStageTests.{A_soft_delete_whose_record_can_still_be_read_is_not_confirmed, Recovery_is_read_only_even_when_the_read_itself_fails}、Golden.Storage 检查点序列覆盖
- 任务页模型 3 项、数据页 7 项：新增 Presentation.{TaskJobViewModelTests（3）, CellViewModelTests（4，含 DataUiFixture 拒绝已有文件）, DataTableViewModelTests（4）}
新增测试：Presentation（11）、Application（11：BatchSelection 4、SubmissionHandoff 5、UnknownReview 2；另 TableDensityStore 1）、Review（4）；共 27 个测试方法（含参数化后 32 项运行）
保留的场景及原因：无。DataUiFixture.cs 保留（--seed-data-ui），并链接进 VamSys.UnitTests 用于其拒绝测试；Program.cs 命令行入口不变
项目改动：VamSys.UnitTests.csproj 新增 CommunityToolkit.Mvvm 与对 App 三个源文件、DataUiFixture 的链接，packages.lock.json 随之更新；旧运行器 csproj 未动（对 TaskViewModels／DataTableViewModel 的链接现已无人使用，留给主会话清理）
待主会话处理：tests/VamSys.Tests/README.md 仍列出 --ui-interactions、--task-submission、--data-table 与已删除文件；TEST-COVERAGE.md 的 F05／F08／F13／F14／F18 覆盖矩阵；分析基线未改（175 当前，180 允许）
提交：见下一条 git log（提交说明“清理：C4 四个文件的旧场景迁移到新测试并删除”）