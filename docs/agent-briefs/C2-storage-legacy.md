# C2：存储相关旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c2-storage-legacy`
- 分支：`cleanup/c2-storage-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`StorageScenarios.cs`、`StorageV3Scenarios.cs`、`DraftSaveScenarios.cs`、`HistoryScenarios.cs`、`CredentialSaveScenarios.cs`
- 新测试目录：`tests/VamSys.IntegrationTests/Storage/`、`tests/VamSys.UnitTests/Storage/`

## 说明

**保留：** `StorageProcessScenarios.cs`（真实进程终止恢复），以及它和窗口脚本依赖的 `StorageScenarios.Fixture()`、`StorageScenarios.Legacy(...)`、`--storage-child`、`--draft-child`、`--seed-storage-ui`。
已有可参考的新测试：`IntegrationTests/Storage/Journal*`、`IntegrationTests/Golden/Storage/*`（含旧版本迁移金样）、`UnitTests/Storage/*`、`UnitTests/Domain/EditHistoryTests.cs`、`UnitTests/Application/WorkspaceSessionTests.cs`。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

按共用做法中的格式，不超过 25 行。