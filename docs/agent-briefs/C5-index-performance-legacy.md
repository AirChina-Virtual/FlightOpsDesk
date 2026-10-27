# C5：判重索引与请求次数旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c5-index-performance-legacy`
- 分支：`cleanup/c5-index-performance-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`CandidateIndexScenarios.cs`、`PerformanceScenarios.cs`
- 新测试目录：`tests/VamSys.UnitTests/Api/`、`tests/VamSys.IntegrationTests/Api/`

## 说明

`PerformanceScenarios` 中断言请求次数、查询次数的场景要迁移（请求次数用 `FakeOperationsServer` 计数）；只断言耗时的场景改为不带计时门槛的正确性测试，或在结果中说明为何删除（耗时由 `--benchmark-*` 与窗口脚本衡量）。**保留** `--benchmark-performance` 入口及其依赖的代码。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

按共用做法中的格式，不超过 25 行。