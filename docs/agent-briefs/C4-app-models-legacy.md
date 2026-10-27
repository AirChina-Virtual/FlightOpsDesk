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

按共用做法中的格式，不超过 25 行。