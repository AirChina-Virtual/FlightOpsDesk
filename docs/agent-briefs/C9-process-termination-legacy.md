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

```
删除前／后旧运行器场景数：本组经 `StorageScenarios.Run` 运行的场景删除前 18 个（11 + 3 + 2 + 2；工作树无 v3 基线，所以“v3 二进制拒读”不计，有基线时为 19），删除后 0；`--storage` 入口和 `await StorageScenarios.Run(Test)` 一并删除。未运行完整档，运行器总数（含 C8 的部分）未测。
已删除场景：
- Real process termination and read-only recovery（11 个边界）→ IntegrationTests.Storage.ProcessTermination.ProcessTerminationTests.Ending_the_process_at_a_task_step_or_rebase_boundary_loses_no_progress_and_repeats_no_write，11 条 InlineData：Task:BeforeCommit、Task:AfterCommit、Step:BeforeCommit|POST|InFlight、Step:BeforeCommit|POST|ResponseReceived、Step:AfterCommit|POST|ResponseReceived、Step:BeforeCommit|PUT|InFlight、Step:AfterCommit|PUT|InFlight、PUT:Accepted、Step:BeforeCommit|PUT|ResponseReceived、Rebase:BeforeCommit、Rebase:AfterCommit
- Real process termination during a draft row save（Draft:BeforeCommit、Draft:AfterCommit、completed）→ …Ending_the_process_during_a_draft_row_save_leaves_the_whole_row_save_or_none_of_it，3 条
- Real process termination during SQLite migration（BeforeCommit、AfterCommit）→ …Ending_the_process_during_the_SQLite_migration_leaves_the_old_database_or_the_finished_one_and_a_backup，2 条
- Real process termination during v3 history migration（BeforeCommit、AfterCommit）→ …Ending_the_process_during_the_v3_history_migration_keeps_undo_and_redo_whole，2 条
- actual v3 binary refuses v4 database without changing it → IntegrationTests.Storage.ProcessTermination.V3BinaryRefusalTests.The_actual_v3_binary_refuses_a_v4_database_without_changing_it（从仓库根目录找 artifacts/step5-v3-baseline；不存在则 Assert.SkipUnless 跳过，工作树里就是跳过；Slow）
新增测试：tests/VamSys.IntegrationTests/Storage/ProcessTermination/ProcessTerminationTests.cs（18 个：11 + 3 + 2 + 2）、V3BinaryRefusalTests.cs（1 个，跳过）、ChildProcess.cs（父进程辅助：假服务器、启动与终止、边界匹配）；新项目 tests/VamSys.ProcessChild（Program.cs 的 storage／draft 两种模式、Wire.cs，加入 VamSys.slnx，有 packages.lock.json）。断言逐条保留。
接线：IntegrationTests 以 ProjectReference（ReferenceOutputAssembly=false）保证先构建子进程，构建后把其输出复制到 <输出>/ProcessChild/，父测试用 `dotnet VamSys.ProcessChild.dll` 启动；dotnet 取 DOTNET_HOST_PATH，否则由运行时目录上三级推出（不写死路径）。tests/TestSupport/AssemblyInfo.cs 增加 InternalsVisibleTo("VamSys.ProcessChild")（带屏障的 WorkspaceStore 构造函数是 internal，未改 src/）。每个子进程 30 秒超时，finally 只结束自己启动的进程。
验证：快速档 ALL PASSED（单元 1472 通过 1 跳过；集成 429 通过 1 跳过，不含 Slow）。Slow 单独运行：`dotnet test --project tests/VamSys.IntegrationTests -c Release --no-build -- --filter-trait Category=Slow --filter-class "*ProcessTerminationTests"`，18 通过 0 失败，约 16.5 s（含启动约 17 s）；V3BinaryRefusalTests 1 跳过（无基线）。
回植验证（已撤回）：把恢复后 PUT 次数期望 +1 → 两条续传边界（Step:AfterCommit|POST|ResponseReceived、Step:BeforeCommit|PUT|InFlight）失败；草稿保存的存储版本号期望 +1 → Draft:AfterCommit 与 completed 失败；备份文件名模式改错 → 两条 SQLite 迁移边界失败（BeforeCommit 同样，因备份在提交前已存在）；v3 迁移后重做值改错 → 两条 v3 边界失败。
保留的场景及原因：旧运行器中无保留的进程终止场景。StorageScenarios.cs 保留 Fixture／Legacy／FixedLegacy／LegacyJson／Temp／Open／Sql／Check 与 Benchmark（`--seed-storage-ui`、`--benchmark-storage`、JournalBenchmark 仍在用），FixedId／FixedLegacy 之外无多余内容；README 已更新这两处说明。`--storage-child`／`--draft-child` 入口已删除（按本简报第 4 条，覆盖 C0 的“保留”条款）。
需主会话处理：TEST-COVERAGE.md 里的 X04、J05 等对 StorageProcessScenarios／“实际 v3 二进制”的引用；合并后在主检出实际跑一次 V3BinaryRefusalTests；analyzer 基线（快速档 new-warnings 显示无新增）。
提交：见 git log（分支 cleanup/c9-process-termination-legacy）
```
