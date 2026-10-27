# C11 结果：A 半（第 1–7 条）

旧代码取自 `git show 1c12605^:tests/VamSys.Tests/<文件>.cs`。共核对 12 个旧场景、41 条 `Check(...)`（含合取条件的整条算一条）。
新增 15 个测试（全部 `[Fact]`），均追加在对应文件末尾；无 `Skip`，未改 `src/`、TEST-COVERAGE.md、analyzer-baseline.txt。
快速档 `pwsh scripts/check-all.ps1`：ALL PASSED（单元 1497，集成 446，各跳过 1 个原有项）。

简写：RP = `UnitTests/Api/RequestPlannerTests.cs`，AB = `UnitTests/Api/ApiBasicsTests.cs`，SM = `UnitTests/Domain/SnapshotMergerTests.cs`，RR = `UnitTests/Api/ResourceReaderTests.cs`，SW = `UnitTests/Api/StepWriterTests.cs`，WR = `IntegrationTests/Api/WriteReadbackAndRecoveryStageTests.cs`，PF = `IntegrationTests/Api/PreflightAndDuplicateStageTests.cs`，OT = `IntegrationTests/Transport/OperationsTransportTests.cs`。行号为提交后的行号（取方法起始行附近）。

## 1. 条目 1：Frozen schema hash／Airport aliases

API airport aliases match IATA imports and reject duplicate aliases
  CSV 导入 `LHR` 命中 EGLL 行：行数 1、ICAO 仍为 EGLL、Name 更新 → AB `An_airport_csv_import_finds_the_row_by_either_code_and_refuses_two_rows_for_one_airport_atomically`
  同一批含 LHR 与 EGLL 两行：抛 FormatException 且撤销栈长度不变 → 同上
  旧快照只有 `LHR` 的行与带身份的 EGLL 合并：1 行、无冲突、RemoteId=101 → AB `A_refresh_binds_a_row_saved_under_the_iata_code_to_the_airport_with_that_alias`
回植验证：这些是 C3 已有测试，未新增，没有回植。

Frozen official schema hash and route create integer associations
  内嵌 OpenApi 的 SHA256 等于 `ApiContracts.Sha256` → AB `The_official_schema_shipped_with_the_program_is_the_frozen_one`
  route 创建：路径 `routes`、POST、`fleet_ids` 为整数 [4,5] → RP `Scheduled_route_create_sends_airports_fleets_and_times_in_hh_mm_ss`（body `"fleet_ids":[4,5]`，整数）；LHR 别名解析 → AB `A_route_create_sends_iso_dates_and_the_auto_cost_index...`
  aircraft 创建引用仅本地存在的 fleet：PreviewPlan 一步、Plan 抛 ApiInvalid → 补写 RP `An_aircraft_create_for_a_fleet_that_exists_only_locally_previews_but_is_not_planned`（原有 RP `A_preview_stands_a_placeholder_in_for_a_fleet_that_does_not_exist_yet` 只覆盖 update）
  他人 VA 的行改名：ApiWrongVa → RP `A_row_of_another_airline_is_never_planned`（用 Airports 行，判断与资源无关，旧场景用 Fleets；视为等价）
回植验证：改 Preview 结果为 Empty，测试失败后撤回。

## 2. 条目 2：20,000 snapshots／Three-way refresh

20,000 API snapshots merge without losing row identity or sparse edits
  20000 行、第 100 行本地改、第 200 行远端改：行数 20000、本地值保留、远端值并入、无冲突 → 补写 SM `Twenty_thousand_rows_merge_without_losing_identity_or_either_side_s_edit`（另断言最后一行身份 RemoteId=20000）
  耗时输出（Console.WriteLine）→ 有意不测：性能计时按 README 由主会话统一测量，测试内不断言毫秒数。
回植验证：行数期望改 19999，失败后撤回。

Three-way refresh preserves independent edits, CSV identity and sticky conflicts
  无身份的快照行与草稿行按 ID 列匹配：LocalId 不变、Name=Mine 保留、Type Code 取远端 B739、无冲突 → 补写 SM `A_row_without_identity_is_matched_by_its_id_column_and_each_side_keeps_its_own_field`（原有 `Each_field_is_decided_on_its_own` 用的是带身份的行）
  远端 Name 又变为 Theirs → 冲突 1 条 → 补写 SM `A_remote_value_that_changed_after_a_first_merge_conflicts_with_the_local_edit_once`（并断言 original/mine/theirs 三值）；三方规则本身 → SM `A_field_follows_the_three_way_rule`（theory）
  再合并同一远端行：冲突仍为 1 条 → 同上补写测试后半 + SM `Unresolved_conflicts_survive_another_refresh_for_rows_that_are_still_there`
  机场：快照与草稿都有无身份的 EGLL 行，远端同一机场带身份 101 → 1 行、RemoteId=101 → 补写 SM `A_local_airport_without_identity_binds_by_its_icao_code_even_when_the_snapshot_holds_it_too`（原有 `An_unambiguous_alias_matches_...` 没有快照副本，且匹配的是 IATA 别名）
回植验证：Type Code 期望改 B738、第三值改 Other、RemoteId 改 102，均失败后撤回。

## 3. 条目 3：Current official requests／Airport addressing, clearing, immutability／API messages

Current official requests create capacities atomically and use sparse API fields
  Fleet 创建（含未知列 "unknown"）：一步、POST `fleet`、body 键恰为 name/code/type/max_pax、max_pax=189 → 补写 RP `A_create_leaves_out_a_column_no_resource_knows`（原有 `An_online_fleet_create_must_carry...` 的 body 相同但没有未知列）
  fleet 改名：路径 `fleet/4`、body 只有 name → RP `Numbers_are_sent_as_numbers_and_booleans_as_booleans`（`fleet/4`，只发改动的字段）与 RP `Airport_update_sends_only_the_changed_fields`；旧场景用单字段 Name，新测试用两个字段，稀疏规则同
  Type Code "BAD" 被 ApiInvalid 拒绝 → 补写 RP `A_fleet_type_code_outside_the_contract_never_leaves_the_machine`（原有 `A_fleet_value_outside_the_contract...` 只测 Type 取值）
    有意不同：被报告的字段名为 API 属性名 `code`，不是列名 `Type Code`（合同校验报告 API 属性）；旧场景只断言 code=ApiInvalid，没有断言字段名，新测试按现行行为断言 `code`，不改回列名。
  Workspace 序列化往返后 RawApiJson 不变 → AB `The_raw_api_record_of_a_row_survives_saving_the_workspace`
回植验证：max_pax 期望改 190、字段名期望改 "Type Code"（之前即出现失败），均失败后撤回。

Airport addressing, documented clearing, routing immutability and response precision
  机场改名为 "LHR"（别的机场代码）仍按 `airports/101` 寻址 → 补写 RP `An_airport_renamed_to_the_code_of_another_airport_is_still_addressed_by_its_id`
  Container IDs 清空：body 为空数组 → RP `Clearing_a_field_is_refused_unless_the_field_clears_to_an_empty_array`
  Routing 创建（LHR→EGLL、Tags）：一步、departure_airport_id=101、含 tag → RP `Routing_create_resolves_airport_codes_to_remote_ids`（用 EGLL/lgw，tag 去重为 ["a","b"]）
  Routing 出发机场改为 ZZZZ：ApiImmutable → RP `Routing_airports_cannot_change_after_creation`
  Route 出发时间 00:10 保留服务器秒数 → 00:10:37 → RP `A_time_edited_in_hh_mm_keeps_the_seconds_the_server_stored`
  Route Fleet IDs 改为 1：body 含 fleet_ids → RP `Scheduled_fleet_sets_can_be_replaced_but_not_on_jumpseats_or_when_the_type_changes`
  Route Type 改 jumpseat：ApiJumpseat → RP `A_jumpseat_keeps_its_identity_and_its_type`
  Route 出发机场改 EGKK：ApiImmutable → 补写 RP `The_departure_airport_of_a_route_cannot_change_after_creation`（原有 `Route_airports_cannot_change_after_creation` 改的是到达机场）
  Aircraft 改名：路径 `fleet/4/aircraft/8` → RP `Aircraft_update_addresses_the_aircraft_under_its_fleet`
  Aircraft 改 Fleet ID：PATCH `aircraft/8`、fleet_id=5 → RP `Changing_the_fleet_of_an_aircraft_is_a_patch_with_nothing_else`
回植验证：路径期望改 airports/102、字段期望改 Arr，均失败后撤回。

API messages translate; CSV output remains byte-identical with metadata
  en-US 的 `ApiMoveFleet` 文案含 "Change only the aircraft Fleet ID" → 补写 AB `The_aircraft_move_message_tells_the_user_to_change_only_the_fleet_id`（清单测试只固定键名，不固定文案；RP 只断言消息代码）
  同一变更导出两次字节相同 → 补写 AB `A_csv_export_is_repeatable_and_does_not_depend_on_the_identity_or_raw_record_of_the_row`（CsvGoldenTests 固定的是各资源的导出字节，不含“有无身份／原始 JSON 结果相同”这一性质）
  去掉 After 的 Identity 与 RawApiJson 后导出字节不变 → 同上
回植验证：文案期望加字符、断言改 NotEqual，均失败后撤回。

## 4. 条目 4：Aircraft reading／envelopes

Aircraft reading enumerates fleets and single responses use documented envelopes
  先读 fleet 再读 `fleet/4/aircraft` → RR `Aircraft_are_read_fleet_by_fleet`
  1 行且 Identity.ParentFleetId=="4" → 补写 RR `Aircraft_rows_remember_their_fleet_and_a_single_aircraft_answers_in_data`
  FindAsync(Aircraft,"8") 读取 `{"data":{...}}`，Name=="Plane" → 同上补写（原有测试 RR `A_refresh_reads_everything...` 只断言非空）；Routings 的裸记录 → RR `Routings_answer_a_bare_record_and_everything_else_wraps_it_in_data`
回植验证：Name 期望 Plane2、ParentFleetId 期望 "5"，均失败后撤回。

## 5. 条目 5：Legacy prepared creation persists ID

Legacy prepared creation persists ID before PUT; interrupted writes never repeat POST
  第二步（PUT）发出时，已保存 CreateCompleted 且 RemoteId=="4" → SW `The_created_id_is_saved_before_the_answer_is_parsed_and_the_second_step_goes_to_the_new_record`（saves[3]）；补写 WR `A_prepared_two_step_create_saves_the_new_id_before_the_second_step_and_a_lost_answer_is_settled_by_reading` 在整条流水线上再断言一次
  PUT 超时：状态 Unknown、请求数 3（预检 GET、POST、PUT）→ 补写 WR 同名测试（POST 1 次、PUT 1 次，状态 Unknown；旧场景的预检 GET 在新夹具里同属流水线，用 Calls 计数不另断言）
  再次执行只读回：Succeeded、请求只增加 1 次读、草稿 ID=="4" → 补写 WR 同名测试（Succeeded、POST 仍 1、PUT 仍 1、草稿 ID 列写成新记录 ID 90）；已有 WR `A_legacy_two_step_create_finishes_with_the_missing_step_only` 覆盖的是“第二步从未发出”，不是“发出后答复丢失”
    有意不同：新夹具里服务器记录 ID 为 90（旧脚本为 4）；旧场景用的脚本化响应序列，新场景在拦截器里让模拟服务器应用 PUT 后再抛超时，等价于旧第 4 个响应里 max_pax=189 已存在。
回植验证：已保存 CreateCompleted 期望改 false、草稿 ID 期望改 "91"，均失败后撤回。

## 6. 条目 6：Soft delete 204/404／Online update conflict

Soft delete 204 and exact-ID 404 verify API invisibility without claiming physical erasure
  方法为 DELETE → 补写 WR `A_confirmed_soft_delete_records_that_the_server_accepted_the_write`（断言 DELETE 一次、步骤方法 DELETE）
  状态 Succeeded、`DeletionVerification.Outcome=="ApiInvisible"`、`VerifiedOperations` 含 Airports:Delete → WR `A_soft_delete_that_reads_404_after_the_204_is_proven_and_leaves_the_workspace`
  `WriteAccepted==true` → 补写 WR 上述新测试（状态码 204 同断言）
  不声称物理删除：新体系里没有对应字符串断言，只由 Outcome 名 ApiInvisible／ApiSoftDeleteEvidence 体现 → 无新增。
回植验证：状态码期望 200，失败后撤回。

Online update conflict sends no PUT and persists structured status
  State==Conflict、只有 1 次请求（GET）、Description.Code=="ApiConflict" → PF `A_record_that_changed_on_the_server_since_the_plan_is_a_conflict_and_nothing_is_written`（Calls == ["GET fleet/4"]，Code ApiConflict，并核对 PreflightComparison 差异字段）

## 7. 条目 7：Write 500 not retried／redaction

Write 500 is not retried and response diagnostics redact secrets
  POST 500 只发 1 次 → OT `A_write_answered_with_a_server_error_or_throttle_is_never_resent`
  异常 `Diagnostic` 不含 secret-value → 补写 OT `The_diagnostic_of_a_failed_write_does_not_carry_secrets_from_the_answer`（原有 `Last_response_describes_the_latest_exchange_without_secrets` 只测 201 成功答复的 LastResponse）；同测试另断言 `Details.ResponseBody` 与 `LastResponse.ResponseBody`
回植验证：断言改为 Contains，失败后撤回。

## 汇总

- 核对的旧场景：12 个（条目 1–7），旧断言 41 条。
- 新增测试：15 个（RP 5、SM 4、AB 2、RR 1、WR 2、OT 1）。
- 有意不同：(1) Type Code 校验报告的字段名是 API 属性 `code`；(2) 20k 合并的毫秒输出不再测；(3) 创建后记录 ID 在新夹具里是 90。
- 产品缺陷：未发现。
- 回植验证：15 个新增测试全部做过（改错期望后失败，撤回后通过）。注意：撤回用复制文件时需刷新修改时间，否则增量构建会沿用旧程序集（本次已处理）。
- 提交说明：`核对 C3：A 半旧断言逐条对应`
