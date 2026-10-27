# C10：补齐 C2、C3 中证据偏弱的对应关系

先读 [简报规则与通用约束](README.md)、[架构速览](../ARCHITECTURE.md) 和 [清理共用做法](C0-cleanup-common.md)。旧场景已经删除，原文从 git 历史里读，不需要改旧运行器。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c10-weak-evidence`
- 分支：`cleanup/c10-weak-evidence`，起点见派活消息

## 要补的四处

旧代码读法：`git show <提交>^:<路径>`。

1. **未知写入后“重启再读”**：读 `1c12605^:tests/VamSys.Tests/AuditScenarios.cs`，场景 `Unknown recovery survives restart and never writes: <fault>`（约第 29–60 行）。
   - 原场景在写入失败后保存工作区 JSON，再从 JSON 重建工作区和执行器（模拟重启），然后只读恢复。
   - 新测试 `tests/VamSys.IntegrationTests/Api/AuditRegressionTests.cs` 的 9 种故障没有做这一步。
   - 补上：用真实 `WorkspaceStore`（`TempDataDirectory`）保存，再新建一个 store 读回，用新建的执行器恢复。逐条保留原断言，并断言恢复期间写入请求数为 0。
2. **`cancel` 故障**：同一场景的 `cancel` 分支，即写入进行中取消。
   - 断言任务状态为 `Canceled`、该项为 Unknown（或原场景断言的状态），且重启恢复后不重发。
   - 并入第 1 项的数据行即可。
3. **跨航司机场参与恢复**：同文件 `foreach(var mode in new[]{"ambiguous","foreign","missing"})`（约第 156–170 行）的 `foreign` 分支。
   - 新测试只写了 ambiguous 和 missing。
   - 补上 foreign，逐条保留原断言（是否允许推荐恢复 ID、写入次数、状态）。
4. **草稿保存移交**：读 `7b05b3c^:tests/VamSys.Tests/DraftSaveScenarios.cs` 中关于待保存编辑移交（pending edits hand-over）的场景。
   - 逐条列出它的断言，再与 `tests/VamSys.UnitTests/Application/DraftEditsTests.cs` 中现有测试的断言逐条比对。
   - 原场景经过真实 `WorkspaceStore` 行级保存的部分，在 `tests/VamSys.IntegrationTests/Storage/StorageRowSaveTests.cs` 中补写。
   - 已覆盖的，在结果中写出“旧断言 → 新测试中的哪一行断言”。

## 约束

- 不修改 `src/`。发现产品缺陷：只记录，不修；对应测试用 `Skip` 并写明原因。
- 补写的测试必须先在当前代码上通过；每处至少做一次回植验证（临时改错期望值，确认测试失败，然后撤回），在结果里记录。
- 不修改 `docs/TEST-COVERAGE.md`、`docs/analyzer-baseline.txt`；不新增告警（快速档的 new-warnings 一步必须通过，注意 `StringComparison` 和 `TestContext.Current.CancellationToken`）。
- 运行 `pwsh scripts/check-all.ps1`（快速档），提交说明：`补证据：未知写入重启恢复、取消、跨航司机场恢复、草稿保存移交`。
- 约 30–60 步。

## 结果（由子代理填写）

```
删除前／后旧运行器场景数：不适用（本简报不改旧运行器，C2、C3 之后旧运行器已无场景）。
已补证据（旧场景 → 新测试）：
1. 未知写入后“重启再读”：Unknown recovery survives restart and never writes: <fault>
   → AuditRegressionTests.An_unknown_write_stays_unknown_across_a_restart_and_recovery_after_the_restart_never_writes（10 个数据行）。
   首次运行 PUT 返回 500 得到 Unknown；恢复用的 GET 按故障失败，持久化回调用真实 WorkspaceStore（TempDataDirectory）保存；
   再用新建 store 读回、新建 adapter/executor 恢复。断言：失败后仍为 Unknown；401/403 为 Paused；cancel 为 Canceled；
   重启后第一次恢复仍为 Unknown 且写入请求数仍为 1（只有首次那次 PUT）；服务器改成已应用后变为 Succeeded，写入数仍为 1。
2. cancel 故障：并入上一条的数据行 "cancel"（GET 中途取消令牌并抛 OperationCanceledException，断言 job 为 Canceled，重启后不重发）。
3. 跨航司机场参与恢复：Routing_recovery_with_ambiguous_foreign_or_unknown_airports_cannot_confirm_the_record 增加 "foreign"
   （机场 101 的身份改为他航司连接 "8"；断言 Unknown、未 Rebased、VerifiedOperations 为空、写入数 0）。
   该用例用新建 adapter 执行（旧场景也是替换身份后重建 Api；沿用旧 adapter 时机场索引是旧的，会误判为成功）。
4. 草稿保存移交（DraftSaveScenarios 的 "Pending edits hand over..."，纯 DraftEdits，不经过 WorkspaceStore，故没有要补进 StorageRowSaveTests 的行级部分）：
   - 空记录不允许行保存 → DraftEditsTests.Nothing_recorded_means_nothing_to_save（CanSaveRows false）
   - 记录后本工作区可行保存、其他工作区不可 → Row_saves_are_only_allowed_for_the_workspace_that_was_edited
   - Take 后原记录不可行保存、taken 可、Rows 为该行 → Take_hands_over_the_record_and_leaves_an_empty_one...（118、116、117 行）
   - taken 的 History.Upserts 长度为 0（无撤销步骤时）→ 旧测试未覆盖，新增 DraftEditsTests.A_restored_hand_over_with_edits_in_two_resources_is_retried_in_full_and_the_flag_is_handed_on_once
   - Restore 后 RequiresFullSave、不可行保存 → After_a_failed_save_the_rows_come_back...（154–155 行）
   - Restore 后 Changes.Count==2（两个资源）→ 同上新增测试
   - 再 Take：retried.RequiresFullSave、原记录标志清零、Changes 为空 → 同上新增测试（原有 Take_keeps_the_need... 只覆盖“两个工作区”路径）
   - 两个工作区的编辑要求全量保存 → An_edit_in_another_workspace_forces_a_full_save_and_never_a_row_save
新增测试：AuditRegressionTests.cs（新增 10 + 1 个数据行）、DraftEditsTests.cs（1）
回植验证：把 cancel 期望改为 Paused、重启后首次恢复期望改为 Succeeded、注释掉 foreign 的 WithIdentity、
  把两资源 Changes 数改为 1 → 上述 10 个重启数据行（cancel 同时因期望 Paused 而失败）、foreign 数据行、DraftEdits 新测试全部失败；已撤回。
产品缺陷：未发现。
保留的场景及原因：无。
提交：见 git log（分支 cleanup/c10-weak-evidence）。
快速档：ALL PASSED（单元 1485 通过/1 跳过，集成 443 通过/1 跳过，new-warnings 无新增）。
```
