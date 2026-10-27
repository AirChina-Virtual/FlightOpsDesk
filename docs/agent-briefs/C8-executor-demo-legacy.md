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

删除前／后旧运行器场景数：Program.cs 自带 13 个 → 0 个；运行器只剩 `StorageScenarios.Run`（含 `StorageProcessScenarios`，归 C9）的场景。未运行旧运行器，总数未实测。Program.cs 从 217 行减到约 90 行，只剩命令行入口、`await StorageScenarios.Run(Test)` 和汇总输出。
已删除场景：
1. SQLite workspace isolation, persisted undo, and DPAPI → `WorkspaceIsolationTests.Changing_draft_undo_history_secret_and_tasks_of_one_workspace_leaves_the_other_byte_for_byte_unchanged`（新增，F01）、`WorkspaceIsolationTests.A_saved_undo_step_survives_reopening_and_a_workspace_without_secret_has_none`
2. Batch success and resume → `BatchExecutorTests.A_successful_batch_writes_once_and_running_the_finished_job_again_sends_nothing`
3. Timeout after create → `BatchExecutorTests.A_timed_out_create_stays_unknown_and_running_again_does_not_resend_it`
4. Remote conflict → `BatchExecutorTests.A_remote_change_since_planning_marks_a_conflict_and_nothing_is_written`
5. Cross-resource creates → `BatchExecutorTests.A_dependent_create_is_written_with_the_ID_the_server_returned_for_its_parent`
6. Persist failure → `BatchExecutorTests.A_failing_progress_save_surfaces_as_a_persistence_error_and_no_write_is_sent`
7. Cancellation / failed dependency → `BatchExecutorTests.A_failed_parent_skips_its_child_and_a_cancelled_run_leaves_pending_items_untouched`
8. 401/403 pauses → `BatchExecutorTests.Access_denied_pauses_the_batch_and_the_later_rows_are_never_sent`（用 `JobStatus.Paused` 代替旧的中文文案判断）
9. Recovered create → `BatchExecutorTests.A_create_recovered_with_its_stored_remote_ID_is_verified_by_reading_and_not_written_again`
10. Partial failure → `BatchExecutorTests.A_failure_lets_independent_rows_finish_and_after_repair_only_the_skipped_child_is_sent`（逐步断言写入次数 2 → 4）
11. Documented capabilities / unverified adapter → `UnverifiedAdapterTests.Every_documented_resource_is_declared_readable_and_writable`、`UnverifiedAdapterTests.The_legacy_unverified_adapter_rejects_a_write_for_every_resource`
12. Language preference → `WorkspaceIsolationTests.The_language_defaults_to_Chinese_persists_across_stores_and_an_invalid_value_falls_back_to_Chinese`（另有 `StorageMigrationTests` 对 `LoadLanguage` 的断言）
13. Language switch during execution → `BatchExecutorTests.Switching_language_during_execution_neither_resends_nor_changes_the_task_and_the_texts_follow_the_language`
同时删除只被这些场景使用的 `CountingService`、`AccessDeniedService`，以及无人引用的 `FakeHttp`、`Sync`、`Check` 和未使用的 `csv`、`planner`。
新增测试：`tests/VamSys.UnitTests/Application/BatchExecutorTests.cs`（9）、`tests/VamSys.UnitTests/Api/UnverifiedAdapterTests.cs`（2）、`tests/VamSys.IntegrationTests/Storage/WorkspaceIsolationTests.cs`（3）。`CountingService`／`AccessDeniedService` 的测试版本是 BatchExecutorTests 内的私有嵌套类。
保留的场景及原因：无剩余项。`Row` 辅助函数仍被 `--seed-*` 入口使用。
快速档：ALL PASSED（单元 1484 通过 1 跳过，集成 432 通过 1 跳过，无新增警告）。未发现产品缺陷。
提交：见 git log（分支 cleanup/c8-executor-demo-legacy）。
