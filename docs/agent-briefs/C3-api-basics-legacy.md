# C3：API 基础、契约与审核回归旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c3-api-basics-legacy`
- 分支：`cleanup/c3-api-basics-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`ApiScenarios.cs`、`ContractScenarios.cs`、`AuditScenarios.cs`、`ReauditScenarios.cs`、`LifecycleScenarios.cs`
- 新测试目录：`tests/VamSys.IntegrationTests/Api/`、`tests/VamSys.UnitTests/Api/`

## 说明

已有可参考的新测试：`UnitTests/Api/*`、`UnitTests/Transport/*`、`IntegrationTests/Api/*`、`IntegrationTests/Transport/*`、`IntegrationTests/Golden/Requests/*`。审核回归场景（Audit、Reaudit）各对应一个以前修过的问题：迁移时每个问题至少保留一条以问题命名的回归测试。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

按共用做法中的格式，不超过 25 行。