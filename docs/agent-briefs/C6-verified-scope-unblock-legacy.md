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

**另外补一个端到端测试**（清理 C4 时被拆散、未按原样重建）：在 `tests/VamSys.IntegrationTests/Api/` 里，用真实 `WorkspaceStore`（隔离目录）、真实执行器和 `FakeOperationsServer`，经 `SubmissionService` 提交 3 条 scheduled 航线软删除：断言只启动本次提交的任务、每条只发一次 DELETE；再让其中一条以超时结束为 Unknown，执行只读恢复，断言恢复期间写入请求数为 0。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

删除前／后旧运行器场景数：`await test(` 调用的静态计数 64 → 32（两个文件共 32 项：Unblock 11、VerifiedScope 21；未运行旧运行器，它不在快速档内）。`--verified-scope`、`--unblock` 入口及 Program.cs 的 4 处调用已删除；两个文件无被其他文件引用的夹具，已整体删除。
已删除场景（新测试均在 IntegrationTests/Api 或 UnitTests 下；`Cov.` 表示早已存在的覆盖）：
- F1 放弃未发出 → DiscardUnsentTests.{Discarding_never_sent_items…, Only_items_that_never_reached_the_server…}；F1 任务页 → TaskRecoveryPresentationTests.{Discarded_items_are_shown_apart…, A_demo_task_is_never_offered_discarding}
- G6 读回判定 → Cov. ReadbackVerifierTests（字段、缺记录、父机队、机场别名、jumpseat 窗口）、UnknownResolverTests.The_created_record_must_still_hold…；新增 RecoveryEvidenceRulesTests.A_resumed_create_is_compared_on_its_post_values_alone…
- G1 接受服务器当前值 → AcceptServerValueTests.{An_accepted_write_whose_read_back_differs…(update/create/delete), The_server_value_is_accepted_only_after…(3), Soft_deletes_moves_writes_without_a_receipt…(4)}
- G2b/G2a 无回执软删除 → UnansweredSoftDeleteTests.{…reads_404_can_be_confirmed_by_hand…(2), A_record_that_is_still_readable…(3), A_hard_delete_is_never_offered…, …marked_not_applied(2), …never_marked_not_applied(4)}
- A1 共用 IATA 刷新 → Cov. SnapshotMergerTests.A1_two_remote_airports…、RequestGoldenTests.Refresh_two_airports_with_the_same_iata
- 外部证据范围、不旁路预览／Pending → ExternalEvidenceReviewTests（全部）、Cov. FieldSpecTests.Evidence_covers_follows_the_table
- 飞机移动（单 PATCH、500 恢复、检查点中断、拒绝、错读回）→ AircraftMoveTests（全部，写入次数均断言）、Cov. RequestPlannerTests 移动用例；新增 RecoveryEvidenceRulesTests.A_move_cleared_to_no_fleet_is_invalid…
- 机场／scheduled／jumpseat 204/404 → SoftDeleteVerificationTests.Airport_scheduled_and_jumpseat_deletes…；证据链各环节 → SoftDeleteVerificationTests.A_read_only_check_completes_a_delete_only_when…(10)、Cov. DeletionVerifierTests
- 历史只读核对（升级、200/500/403、检查点失败、人工确认）→ SoftDeleteVerificationTests 其余 4 项
- 数字 Client ID、令牌失败／401 回 Pending、未知写入只读证明、旧两步创建 → UnreachedAndUnknownWriteTests（全部）、Cov. WriteReadbackAndRecoveryStageTests、UnknownResolverTests、ClientCredentialsTests
- 连接识别 VA → Cov. ResourceReaderTests（ranks／containers）；新增 RecoveryEvidenceRulesTests.Connecting_with_credentials_of_another_airline…（ApiWrongVa）
- 旧 JSON 可选字段／FormatVersion==4 → RecoveryEvidenceRulesTests.New_deletion_evidence_is_optional_json…；任务页人工确认计数 → TaskRecoveryPresentationTests.Manual_confirmations_are_counted_apart…
新增测试：IntegrationTests/Api：UnansweredSoftDeleteTests（12）、SoftDeleteVerificationTests（19）、AcceptServerValueTests（10）、DiscardUnsentTests（2）、AircraftMoveTests（12）、UnreachedAndUnknownWriteTests（14）、SubmitAndRecoverScheduledDeletesTests（1，端到端：真实 WorkspaceStore＋SubmissionService＋执行器＋FakeOperationsServer；3 条 scheduled 删除只启动本次任务、每条 DELETE 一次，一条超时为 Unknown 后只读恢复写入 0；另有旧 Pending 任务不被启动的断言）；辅助 StoredWorld.cs。UnitTests：Application/ExternalEvidenceReviewTests、Application/TaskRecoveryPresentationTests、Api/RecoveryEvidenceRulesTests。所有故障路径均先注册记录并计数断言“故障确已触发”。
保留的场景及原因：无剩余项。注意：tests/VamSys.Tests/README.md 仍提到 `--verified-scope` 与这两个文件（未改，避免与并行简报冲突，合并时请更新）；docs/TEST-COVERAGE.md 的 F16／F17／I11／I13／A1 行待主会话改写。
观察：新测试用真实数据库，目录释放会清空全进程 SQLite 连接池，曾使无关的 StorageHistoryTests 偶发 ObjectDisposedException，故这些类放入禁用并行的集合 StoredWorldCollection（既有的 StorageExecutionPersistenceTests 仍有同类小概率风险，未改 TestSupport）。未发现产品缺陷。
快速档：ALL PASSED（单元 1472，集成 381）。
提交：见下方 git log。