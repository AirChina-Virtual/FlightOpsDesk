# C7：10 月兼容性旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c7-live-compatibility-legacy`
- 分支：`cleanup/c7-live-compatibility-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`LiveCompatibilityScenarios.cs`、`RouteApprovalFixture.cs`、`Fixtures/vcca-weight-20261020.json`
- 新测试目录：`tests/VamSys.IntegrationTests/Api/`、`tests/VamSys.IntegrationTests/Fixtures/`

## 说明

真实响应回放 `Fixtures/vcca-weight-20261020.json` 是保留素材：移到 `tests/VamSys.IntegrationTests/Fixtures/`，由新测试读取。**保留** `--export-diagnostic-replay` 入口：若它依赖本文件的代码，把所需部分移到一个小文件中保留。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

按共用做法中的格式，不超过 25 行。