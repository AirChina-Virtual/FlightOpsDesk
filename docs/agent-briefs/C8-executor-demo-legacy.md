# C8：Program.cs 里剩余的 13 个演示执行器与存储场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c8-executor-demo-legacy`
- 分支：`cleanup/c8-executor-demo-legacy`，起点见派活消息
- C9 在并行处理 `StorageProcessScenarios.cs` 和 `StorageScenarios.cs`；本简报只改 `tests/VamSys.Tests/Program.cs` 中的 `await Test(...)` 场景及其专用辅助类，以及下面的新测试目录。

## 负责的场景（均在 `tests/VamSys.Tests/Program.cs`）

1. SQLite workspace isolation, persisted undo, and DPAPI
2. Batch success and resume do not duplicate writes
3. Timeout after create stays unknown and never blindly retries
4. Remote conflict blocks overwrite
5. Cross-resource creates resolve returned IDs before dependent writes
6. Persist failure stops all writes
7. Cancellation stops pending writes, failed dependency skips child
8. 401/403 pauses batch without sending later rows
9. Recovered create with persisted remote ID verifies without repeat
10. Partial failure permits independent work; repaired dependency resumes child only
11. Documented capabilities available; legacy unverified adapter rejects writes
12. Language preference persists in the store and an invalid value falls back to Chinese
13. Language switch during execution does not resend writes or change task state

新测试目录：`tests/VamSys.UnitTests/Application/`、`tests/VamSys.IntegrationTests/Api/`、`tests/VamSys.IntegrationTests/Storage/`。

## 说明

- 这些场景用 `DemoService` 和 `CountingService` 驱动 `BatchExecutor`。很多行为在 R5 拆分后已有新测试（执行器、恢复、权限暂停），要逐条按断言核对，不能只按测试名对上；涉及“不重发写入”的，新测试必须断言写入次数。
- **场景 1 必须新增一个专门的多工作区隔离测试**（覆盖矩阵 F01 目前只有间接证据）：同一个 `WorkspaceStore`（`TempDataDirectory` 隔离目录）里保存两个工作区，对其中一个改草稿、撤销历史、密钥和任务，重新打开存储后断言另一个工作区的序列化结果逐字节不变，且 `LoadSecret` 互不可见。DPAPI 部分只在 Windows 上运行。
- 场景 13 的文本键 `Text_140197D868` 和英文文案以当前资源为准；资源键已被清单测试锁定，不要改。
- 删除场景后，`CountingService`、`AccessDeniedService` 等只被这些场景使用的辅助类一并删除；仍被命令行入口或 `StorageScenarios` 使用的保留。
- 删除后 `Program.cs` 应只剩命令行入口、`await StorageScenarios.Run(Test)` 和汇总输出。

## 完成标准

- 13 个场景全部迁移并删除，或在结果中列出剩余项及原因。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）
