# C6：已验证范围与解除卡死旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c6-verified-scope-unblock-legacy`
- 分支：`cleanup/c6-verified-scope-unblock-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`VerifiedScopeScenarios.cs`、`UnblockScenarios.cs`
- 新测试目录：`tests/VamSys.IntegrationTests/Api/`、`tests/VamSys.UnitTests/Api/`、`tests/VamSys.UnitTests/Application/TaskRecoveryServiceTests.cs` 所在目录

## 说明

这两个文件覆盖飞机移动、软删除 204／404、历史只读核对、放弃未发出、接受服务器当前值、无回执删除确认等写入安全行为，是最重要的一批。每条断言都要有对应；涉及“不重发写入”的，新测试必须断言写入请求次数。`--unblock`、`--verified-scope` 入口随文件一起删除。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

按共用做法中的格式，不超过 25 行。