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

删除前／后旧运行器场景数：删除 55 个（Api 12、Contract 5、Audit 22、Reaudit 8、Lifecycle 8）；运行器总数未测。`Legacy/` 目录和所有 pragma 已移除，快速档 ALL PASSED（单元 1434、集成 280）。
已有测试覆盖（按测试名对应，未逐条核对每个断言字面值；重启后再读一类以存储/检查点测试为准）：
- Frozen schema hash → 新增 ApiBasicsTests；Airport aliases → 新增 ApiBasicsTests + DuplicateFinderTests.An_airport_is_a_duplicate_when_the_code_is_its_icao_or_its_iata_in_any_case
- 20,000 snapshots merge／Three-way refresh → SnapshotMergerTests（UnitTests/Domain）
- Current official requests／Airport addressing, clearing, immutability／API messages → RequestPlannerTests（Airport_*、Routing_*、Route_*、Changing_the_fleet_of_an_aircraft_is_a_patch…、A_time_edited_in_hh_mm_keeps_the_seconds…）、CsvGoldenTests、RequestGoldenTests
- Aircraft reading／envelopes → ResourceReaderTests.Aircraft_are_read_fleet_by_fleet、Routings_answer_a_bare_record…
- Legacy prepared creation persists ID → StepWriterTests.The_created_id_is_saved_before…、WriteReadbackAndRecoveryStageTests.A_legacy_two_step_create_finishes_with_the_missing_step_only
- Soft delete 204/404 → WriteReadback…A_soft_delete_that_reads_404_after_the_204_is_proven…；Online update conflict → PreflightAndDuplicateStageTests.A_record_that_changed_on_the_server_since_the_plan_is_a_conflict…
- Write 500 not retried／redaction → OperationsTransportTests.A_write_answered_with_a_server_error_or_throttle_is_never_resent、Last_response_describes_the_latest_exchange_without_secrets
- Spec-shaped routes／Only routes may omit airline_id → ResourceMapperTests.A_route_has_no_airline_in_its_record…、Resources_other_than_routes_must_name_their_airline、RequestGoldenTests.Refresh_all_resources_with_pagination
- Created route ID saved before parse fails → StepWriterTests.An_answer_with_an_id_but_an_unreadable_record_still_leaves_the_id_saved
- Throttled writes → WriteReadback…A_rate_limited_write_goes_back_to_pending…、RequestGoldenTests.Write_429_returns_to_pending…
- Airport duplicate rejects both aliases／resolved local dependency → RequestGoldenTests.Create_blocked_when_duplicate_check_finds_same_airport_alias、PreflightAndDuplicateStageTests.A_local_reference_is_replaced_by_the_remote_id…
- Preflight 500 retryable／Post-write verification errors stay unknown → PreflightAndDuplicateStageTests.A_server_that_cannot_answer_the_preflight_read_leaves_the_item_pending、WriteReadback…A_read_back_that_fails_leaves_the_item_unknown_and_a_later_read_confirms…
- Full refresh atomic／Rotated credentials cannot change the bound VA／Paused task rotates credentials → ResourceReaderTests.A_refresh_*（4 条）、Reads_that_need_the_session_airline_identify_it_once_and_refuse_a_mismatch、WriteReadback…A_rejected_login_during_the_write_pauses_the_job…、CredentialSaveTests
重写为普通 xunit 测试：共 52 个用例（15 个方法）
- tests/VamSys.IntegrationTests/Api/AuditRegressionTests.cs（38 用例）：Unknown recovery（9 种故障）、Interrupted partially written（3）、Recovery not downgraded（2）、Airport recovery aliases（2）、Routing recovery ambiguous/missing（2）、Recovery candidate corrected、Routings/Routes readback by airport ID（6）、duplicate by airport reference（6）、Typed create readback（3）、Sparse typed update（2）、Explicit zero/empty collection、delete 后缓存清除。
- tests/VamSys.UnitTests/Api/ApiBasicsTests.cs（14 用例）：schema hash、airport CSV alias、alias 合并、RawApiJson 往返、路由校验、cost index（8）、ISO 日期。
无法放置的：无。局限：Unknown recovery 的“重启后再读”和“cancel”故障、跨 VA 的 Routing 恢复（foreign）未单独重写，分别以存储检查点测试和 ReadbackVerifierTests.Rows_of_another_airline_are_refused_in_both_directions 为准。
保留：`ScriptedHttp`、`AuditHttp` 仍被其他旧场景使用，移到 tests/VamSys.Tests/LegacyHttpFixtures.cs。未改 src/、TEST-COVERAGE.md、analyzer-baseline.txt。
