# C9：真实进程终止恢复与 v3 旧程序场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c9-process-termination-legacy`
- 分支：`cleanup/c9-process-termination-legacy`，起点见派活消息
- C8 在并行处理 `Program.cs` 里的 `await Test(...)` 场景；本简报只改 `StorageProcessScenarios.cs`、`StorageScenarios.cs`、`Program.cs` 中的 `--storage-child`／`--draft-child` 入口和 `StorageScenarios.Run` 调用，以及下面的新文件。

## 负责的场景

- `StorageProcessScenarios.cs`：4 组参数化场景，即 11 个任务／步骤／rebase 边界、3 个草稿保存边界、2 个 SQLite 迁移边界、2 个 v3 历史迁移边界。
- `StorageScenarios.cs`：`actual v3 binary refuses v4 database without changing it`。

## 做法

1. **子进程宿主**：新建一个小控制台项目 `tests/VamSys.ProcessChild`（加入 `VamSys.slnx`，`net10.0`，按其他测试项目的方式启用锁文件），把 `StorageProcessScenarios.Child` 和 `DraftChild` 的逻辑及其依赖的线协议（`Wire`、屏障输出）移进去，入口参数保持 `storage`／`draft` 两种模式。
   - 只移动子进程侧代码，不整体搬运场景。
   - 保持“只用屏障点终止、不在别处插桩”的原则。
   - 不修改 `src/`。如果屏障钩子只能通过 `src/` 里已有的测试接缝接入，就使用那些接缝。
2. **父进程测试**：在 `tests/VamSys.IntegrationTests/Storage/ProcessTermination/` 写 xUnit v3 测试，用 `[Theory]` 加 `[InlineData]`（或 `MemberData`），每个边界一条数据。逐条保留原场景的全部断言：写入次数、Item 状态、`CreateCompleted`、`RemoteId`、步骤状态、快照、撤销计数、`VerifiedOperations`、修复 ID 后的恢复，以及迁移后的 `user_version`、备份文件和 v3 撤销重做。
   - 父进程通过 `dotnet <ProcessChild.dll>` 启动子进程。dotnet 路径取当前进程或 `DOTNET_HOST_PATH`，不要写死。
   - 子进程宿主的输出按项目引用复制到测试输出目录，或在测试里定位，选一种稳定的做法。
   - 每条测试设置超时，`finally` 里只终止自己启动的进程。
   - 整组标记 `[Trait("Category", "Slow")]`。
3. **v3 旧程序**：迁移为集成测试，要求存在 `artifacts/step5-v3-baseline/tests/VamSys.Tests.dll`（从仓库根目录定位），不存在时用 `Assert.Skip` 写明原因；标记 Slow。
   - 工作树里没有这个基线，所以测试会被跳过，这是正常的。
   - 主会话合并后会在主检出里实际跑一次。
4. 删除旧场景、`--storage-child` 和 `--draft-child` 入口；`StorageScenarios.cs` 剩下的内容若只供 `--benchmark-storage` 等命令行入口使用，就保留这部分，`StorageScenarios.Run` 和它的调用删除。
5. 同步更新 `tests/VamSys.Tests/README.md` 中关于这两个入口的说明。其他文档由主会话处理。

## 验证

- 快速档会排除 Slow，所以除了快速档，还要单独运行一次：`dotnet test tests/VamSys.IntegrationTests -c Release -- --filter-trait Category=Slow --filter-class <新测试类>`（不要加 `-nologo`）。
- 结果里写明通过数和耗时。
- 每组至少做一次回植验证，在结果里记录：比如让父测试把期望的 Puts 次数加 1，确认对应边界会失败，然后撤回。

## 完成标准

- 4 组和 v3 场景全部迁移并删除，旧运行器场景数变为 0（或只剩 C8 未完成的部分）。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束，Slow 组单独运行通过。

## 结果（由子代理填写）
