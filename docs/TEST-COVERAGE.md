# 需求覆盖矩阵（R7 做法第 1 步，R2 领域层部分）

依据 [重构计划](REFACTOR-PLAN-2026-10-20.md) 第 3.1、3.2 节。每一行是一条需求；“新测试”列是为新结构编写的测试的完整名称，“仍覆盖的旧场景”列是尚未删除、仍在 `tests/VamSys.Tests` 中保护该需求的旧场景。旧场景只在对应需求有了新测试之后才删除，删除前逐条读过它的断言。

状态含义：**完成**＝该需求在所有层都有新测试；**领域完成**＝领域层（含 Application 的纯逻辑）已有新测试，其余层待办；**待办（R3+）**＝尚无新测试，由旧测试保护。

测试名称：行内写成 `命名空间简写.类名.{方法, 方法}`，完整名称由前缀加上简写得到：`Domain.` ＝ `VamSys.UnitTests.Domain.`，`Application.` ＝ `VamSys.UnitTests.Application.`，`Transport.` ＝ `VamSys.UnitTests.Transport.`，`Inventory.` ＝ `VamSys.UnitTests.Inventory.`，以 `IntegrationTests.` 开头的为 `VamSys.IntegrationTests.…`，以 `VamSys.UnitTests.` 开头的已写全（BatchShiftMinutesTests、LocalReferenceTests、RowReplacementTests 在根命名空间）。含 `Theory` 的方法名代表它的全部数据行。

## 3.1 功能

| # | 层 | 新测试 | 仍覆盖的旧场景 | 状态 |
|---|---|---|---|---|
| F01 多工作区，一 VA | Storage、Application | 待办（R3+） | StorageScenarios、ApiScenarios | 待办（R3+） |
| F02 连接设置与凭据 | Application、Infrastructure、UI | 待办（R3+） | CredentialSaveScenarios、test-credentials-ui、test-settings-save-ui | 待办（R3+） |
| F03 测试连接并识别 VA | Infrastructure | 待办（R3+） | ApiScenarios | 待办（R3+） |
| F04 API 刷新、三方合并、冲突、远端删除不重建、刷新后清除撤销历史 | Domain（合并）、Infrastructure（原子刷新）、UI（冲突审阅） | Domain.SnapshotMergerTests.{A_field_follows_the_three_way_rule, Each_field_is_decided_on_its_own, Columns_that_exist_only_in_the_draft_are_kept, A_remote_row_with_a_new_id_is_added_as_it_is_and_local_new_rows_stay, A_row_deleted_remotely_is_not_recreated_when_the_draft_never_changed_it, A_row_deleted_remotely_but_edited_locally_stays_with_a_conflict_on_the_whole_row, A_row_deleted_remotely_and_marked_for_deletion_locally_just_disappears, A_local_deletion_conflicts_only_when_the_remote_row_changed_meanwhile, An_imported_candidate_matches_the_remote_row_by_key_and_differing_values_need_review, Unresolved_conflicts_survive_another_refresh_for_rows_that_are_still_there, A_refresh_replaces_the_snapshot_with_copies_stamps_the_time_and_clears_the_undo_history, Rows_are_matched_by_remote_id_even_when_the_key_column_differs, A1_two_remote_airports_sharing_an_iata_code_do_not_fail_the_refresh_and_the_code_matches_no_local_row, An_unambiguous_alias_matches_a_local_airport_without_identity_and_keeps_the_remote_code}；IntegrationTests.Golden.Requests.RequestGoldenTests.Refresh_two_airports_with_the_same_iata、Refresh_all_resources_with_pagination（R0） | ApiScenarios、ContractScenarios（仅 `updated_at` 变化不算冲突）、LifecycleScenarios（刷新原子换索引）、UnblockScenarios.RunRefresh（A1 端到端） | 领域完成 |
| F05 数据页搜索／筛选／分页／密度 | Presentation、UI | 待办（R6） | DataTableScenarios、test-data-performance-ui、test-ui-interactions | 待办（R3+） |
| F06 单元格编辑（以替换行实现）、同格连续输入、自动保存只写被编辑行 | Domain、Application、Storage、UI | VamSys.UnitTests.RowReplacementTests（R2 已有，全部）；Domain.EditHistoryTests.{Typing_in_the_same_cell_continues_the_last_step_so_one_undo_reverts_it_all, A_continuation_with_nothing_to_continue_changes_the_row_but_records_no_step}；Application.DraftEditsTests（全部）| DraftSaveScenarios、test-cell-editing-ui | 领域完成 |
| F07 撤销／重做：20 步、同格合并、行序恢复、只替换行时行级保存 | Domain、Application、Storage | Domain.EditHistoryTests（全部，含与全快照模型的随机对照 `Delta_history_agrees_with_a_full_snapshot_model_across_mixed_edits_and_json_round_trips`）；VamSys.UnitTests.RowReplacementTests；Application.DraftEditsTests.{After_a_failed_save_the_rows_come_back_and_the_next_save_must_write_everything, An_undo_or_redo_of_replaced_rows_records_their_history_too} | HistoryScenarios（随机对照的存储重载部分、同格续写与异步载荷、迁移、基准）、DraftSaveScenarios、test-v3-verification-ui | 领域完成 |
| F08 批量操作只作用于已选：规则、分配机型、删除、恢复、到期停用；预览显示已选／跳过／未变化／实际变化 | Domain、UI | Domain.RuleEngineTests（全部）；VamSys.UnitTests.BatchShiftMinutesTests（R1 已有）；Domain.FleetAssignmentServiceTests；Domain.DeletionServiceTests.{New_drafts_are_removed_existing_rows_are_marked_and_both_undo_together, Restore_unmarks_only_rows_that_are_marked_in_one_step_and_ignores_the_others}；Domain.ChangePlannerTests.A_draft_row_is_classified_by_its_difference_from_the_baseline | UiInteractionScenarios（预览期间锁定、已选范围）、test-ui-interactions | 领域完成 |
| F09 机型选择器：单选／多选，scheduled 航线替换／追加／移除，最终集合非空 | Domain、Infrastructure（门禁） | Domain.FleetAssignmentServiceTests（全部）；Domain.SchemaValidationTests.{A_jumpseat_route_needs_no_fleet_but_a_scheduled_one_does, An_aircraft_needs_exactly_one_known_fleet}；IntegrationTests.Golden.Requests.RequestGoldenTests.{Route_scheduled_fleet_ids_replace, Route_scheduled_fleet_ids_append, Route_scheduled_fleet_ids_remove, Aircraft_move_between_fleets_uses_patch}（R0） | VerifiedScopeScenarios、LiveCompatibilityScenarios（跨机队移动、集合开放条件） | 领域完成 |
| F10 新机队选择批准且未受限的航司参数 | Domain、Infrastructure | Domain.ChangePlannerTests.Only_online_fleet_creation_needs_the_airline_parameter；IntegrationTests.Golden.Requests.RequestGoldenTests.Fleet_create_with_parameter_ids（R0）；Domain.FieldSpecTests.Jumpseat_identity_and_create_only_fields_are_blocked_on_update（`Parameter IDs` 仅创建） | LiveCompatibilityScenarios（参数批准与只读加载） | 领域完成 |
| F11 CSV 导入：待检查修改、不覆盖快照、表头映射、未知列、三态字段 | Domain | Domain.CsvAdapterTests.{Parse_*, Map_renames_headers_and_rejects_names_that_collide_or_are_blank, Baseline_import_*, Import_writes_the_local_reference_prefix_in_lowercase_whatever_the_file_used, Patch_import_*, Rows_missing_from_a_patch_are_not_deletions_only_an_explicit_delete_flag_is}；Domain.ChangePlannerTests.{A_draft_row_is_classified_by_its_difference_from_the_baseline, Only_changed_fields_are_planned_and_an_empty_value_is_Clear_not_unspecified, The_delete_flag_is_never_a_planned_field, Planned_items_carry_detached_copies_of_the_baseline_and_the_draft, Planning_keeps_draft_order_and_ignores_unchanged_rows, Planning_matches_a_straightforward_full_row_oracle}；Domain.SchemaValidationTests（全部）；IntegrationTests.Golden.Csv.CsvGoldenTests.{Imported_rows_become_the_same_drafts_for_every_resource, Rejected_input_gives_the_same_errors}（R0） | ApiScenarios、AuditScenarios（API 层的 Unknown 恢复，非导入） | 完成（领域与金样） |
| F12 CSV 导出：按资源、仅变化记录、保留未知列、超 10 MB 拆分、字节格式不变 | Domain、Application（任务标记“待到 Orwell 导入”） | Domain.CsvAdapterTests.{Export_*, A_planned_export_can_be_read_back_into_equal_rows}（含默认 10 MiB 拆分 `Export_splits_by_default_below_ten_mebibytes_per_file`）；IntegrationTests.Golden.Csv.CsvGoldenTests.Export_bytes_are_the_same_for_every_resource（R0） | AuditScenarios（任务标记与导出入口）、UiInteractionScenarios | 领域完成 |
| F13 工作区审阅：后台构建、分组、本次提交／暂缓／受阻、依赖暂缓、内容键 | Domain（依赖）、Application、UI | Domain.ChangePlannerTests.{A_known_local_reference_is_replaced_by_the_remote_key_and_remark_text_is_left_alone, The_local_prefix_is_recognised_in_any_case_when_resolving_and_when_depending, Several_references_to_the_same_new_row_make_one_dependency_and_each_kind_adds_its_own}（依赖关系部分） | UiInteractionScenarios（审阅、样本与依赖、内容键） | 待办（R3+） |
| F14 提交交接 | Application、UI | 待办（R4） | TaskSubmissionScenarios | 待办（R3+） |
| F15 在线执行：判重、冲突检查、回读、检查点、未知项、401／403、429 | Application、Infrastructure | 传输层完成（R5 第 1 项）：Transport.ResponseClassifierTests（全部）、IntegrationTests.Transport.OperationsTransportTests.{Get_is_not_retried_after_a_client_error, Authentication_refusal_is_reported_once_and_drops_the_token, Validation_failure_exposes_the_field_errors, Throttled_write_makes_the_next_request_wait_for_retry_after, A_write_that_fails_before_it_is_sent_is_wrapped_as_not_sent, Token_refusal_before_a_write_keeps_the_access_error_inside}；判重、检查点、状态表待办（R4）；IntegrationTests.Golden.Requests.RequestGoldenTests.Write_*（R0） | ApiScenarios、StorageProcessScenarios、ReauditScenarios、AuditScenarios、BatchExecutor 的演示执行器场景（Program.cs） | 待办（R3+） |
| F16 未知项出路 | Application、UI | 待办（R4） | UnblockScenarios、VerifiedScopeScenarios | 待办（R3+） |
| F17 删除：本地引用检查；204／404 自动核对 | Domain（引用检查）、Infrastructure（核对） | Domain.DeletionServiceTests（全部）；Domain.SchemaValidationTests.{Deleting_is_only_valid_for_rows_that_exist_remotely, Deleting_a_row_that_other_rows_still_use_is_an_issue_on_the_deleted_row} | VerifiedScopeScenarios、UnblockScenarios（204／404 与只读核对） | 领域完成 |
| F18 任务中心 | Presentation | 待办（R6） | TaskViewModelScenarios、test-v3-verification-ui | 待办（R3+） |
| F19 诊断导出 | Infrastructure | 脱敏、1 MiB 截断、哈希的行为未变，仅文件移到 `Transport/`；新增 IntegrationTests.Transport.OperationsTransportTests.Last_response_describes_the_latest_exchange_without_secrets；导出与截断单元测试待办（R5 后续） | LiveCompatibilityScenarios | 待办（R3+） |
| F20 维护：备份后事务清理 | Storage | 待办（R3） | LiveCompatibilityScenarios | 待办（R3+） |
| F21 演示工作区与离线模式 | Application、Infrastructure | 待办（R4） | ApiScenarios | 待办（R3+） |
| F22 中英文即时切换、缺失键日志 | Domain、UI | Domain.LocalizationTests（全部）；Domain.LocalizationDiagnosticsTests（R2 已有）；Inventory.ResourceKeyInventoryTests（R0） | Program.cs 语言偏好持久化场景（存储部分）、Program.cs 执行期间切换语言不重发场景、test-localization-ui | 领域完成 |
| F23 存储版本：迁移、拒绝更高版本、版本号防陈旧覆盖、单实例 | Storage、Domain（历史格式） | Domain.EditHistoryTests.{Legacy_full_snapshot_histories_convert_to_the_same_undo_and_redo_states, An_unknown_history_version_or_an_ambiguous_mix_of_formats_is_refused, A_workspace_round_trip_keeps_both_stacks_and_the_cells_they_restore}；IntegrationTests.Golden.Storage.LegacyMigrationGoldenTests、IntegrationTests.Golden.Storage.StorageSequenceGoldenTests、Inventory.PersistedJsonShapeTests（R0） | StorageScenarios、StorageV3Scenarios、HistoryScenarios（迁移、损坏 v3 历史、v3 二进制拒读 v4） | 待办（R3+） |
| F24 安全关闭 | Application、UI | Application.WindowCloseControllerTests（全部） | test-v3-verification-ui、DraftSaveScenarios（关闭保存） | 领域完成 |
| F25 性能统计文件 | Infrastructure | 待办（R5） | PerformanceScenarios | 待办（R3+） |
| F26 正式包与 QA 包 | 打包 | 待办 | PackageChecks | 待办（R3+） |

## 3.2 不变规则

| # | 层 | 新测试 | 仍覆盖的旧场景 | 状态 |
|---|---|---|---|---|
| I01 写入不自动重发；未知只 GET；已有 ID 不再 POST | Application、Infrastructure | IntegrationTests.Golden.Requests.RequestGoldenTests.{Write_timeout_not_applied_is_recovered_read_only_and_never_resent, Write_timeout_applied_by_server_is_confirmed_by_read_without_resend, Create_post_timeout_is_never_resent_during_recovery, Write_429_returns_to_pending_and_is_sent_once_on_resume}（R0）；Transport.ReadRetryPolicyTests（全部，含“写入入口不接受重试策略”的反射检查）；IntegrationTests.Transport.OperationsTransportTests.{A_write_answered_with_a_server_error_or_throttle_is_never_resent, A_write_lost_after_sending_is_reported_as_it_happened_and_never_resent, A_write_cancelled_after_sending_is_not_a_not_sent_error, Get_gives_up_after_three_retries_and_reports_the_last_answer, Get_cancelled_by_the_caller_is_not_retried}（R5）；执行流水线状态表待办（R4） | StorageProcessScenarios、AuditScenarios、ReauditScenarios、Program.cs 演示执行器场景 | 待办（R3+） |
| I02 写后回读比对 | Application、Infrastructure | IntegrationTests.Golden.Requests.RequestGoldenTests（R0 请求序列）；回读比对单元测试待办（R4） | ApiScenarios、ReauditScenarios | 待办（R3+） |
| I03 全进程 ≤60 次／分钟、间隔 ≥1 s、Retry-After | Infrastructure | Transport.RequestBudgetTests（全部：1 秒间隔、61 次 ≥60 秒、同一 VA 共享预算、Retry-After 秒数／日期／过去／缺省、剩余额度 0、排队取消、退避时长、等待计入速率／退避）；IntegrationTests.Transport.OperationsTransportTests.{Transports_of_one_va_share_the_budget_and_stay_one_second_apart, Transports_for_different_vas_do_not_wait_for_each_other, A_queued_read_can_be_cancelled_without_sending, Retry_after_server_budget_and_read_backoff_keep_the_production_waits, Get_retries_wait_the_backoff_and_the_retry_after}；Transport.TokenProviderTests、IntegrationTests.Transport.ClientCredentialsTests | ContractScenarios | 完成（传输层） |
| I04 新建前判重、修改前读最新、删除前检查引用 | Domain（引用）、Infrastructure | Domain.DeletionServiceTests.{A_fleet_is_in_use_under_any_name_an_aircraft_may_use_for_it, Each_reference_in_a_route_fleet_list_is_one_issue_and_other_fleets_do_not_count, An_airport_is_in_use_by_a_route_or_routing_endpoint, An_airport_is_in_use_under_its_codes_its_remote_id_and_its_api_aliases, Imported_info_columns_of_an_airport_also_count_as_names_for_it, Rows_that_are_themselves_marked_deleted_or_are_deleted_together_do_not_block_the_deletion, A_preview_made_before_a_new_reference_appeared_cannot_be_applied}；IntegrationTests.Golden.Requests.RequestGoldenTests.Create_blocked_*（R0） | CandidateIndexScenarios | 领域完成 |
| I05 请求前后先保存；版本号防覆盖；保存失败不改内存 | Application、Storage | Application.DraftEditsTests.After_a_failed_save_the_rows_come_back_and_the_next_save_must_write_everything；Application.WindowCloseControllerTests.A_failed_save_reopens_the_window_unfreezes_it_and_rethrows_so_the_close_can_be_retried | StorageProcessScenarios、DraftSaveScenarios、Program.cs 执行器持久化失败场景 | 待办（R3+） |
| I06 CSV 字节格式不变 | Domain | Domain.CsvAdapterTests.{Export_quotes_a_cell_only_when_it_has_a_comma_quote_or_line_break, Export_then_parse_returns_the_same_cell, Export_writes_utf8_without_bom_and_crlf_endings, Export_orders_columns_key_first_then_required_then_the_rest_as_seen}；IntegrationTests.Golden.Csv.CsvGoldenTests.Export_bytes_are_the_same_for_every_resource（R0） | AuditScenarios | 完成 |
| I07 请求体逐字节不变 | Infrastructure | IntegrationTests.Golden.Requests.RequestGoldenTests（R0，全部） | ApiScenarios、ContractScenarios | 完成（R0 金样） |
| I08 数据库仍为 v4，JSON 字段名与取值不变 | Storage | Inventory.PersistedJsonShapeTests（R0）；IntegrationTests.Golden.Storage.StorageSequenceGoldenTests、IntegrationTests.Golden.Storage.LegacyMigrationGoldenTests、IntegrationTests.RealData.RealDatabaseCopyTests（R0） | StorageScenarios、StorageV3Scenarios、HistoryScenarios | 完成（R0 金样） |
| I09 持久化枚举按数字保存 | Domain | Inventory.EnumInventoryTests.Enum_member_names_and_numeric_values_are_locked（R0） | — | 完成（R0 清单） |
| I10 资源键、MessageDescriptor 代码与参数名不变 | Domain | Inventory.ResourceKeyInventoryTests（R0）；Domain.LocalizationTests.{Both_catalogs_have_the_same_keys_and_a_sizeable_set, Every_text_has_the_same_named_placeholders_in_both_languages_and_english_has_no_chinese} | — | 完成 |
| I11 能力键、契约哈希、外部证据基线不变 | Domain、Infrastructure | Inventory.ConstantsInventoryTests.{Capability_keys_are_locked, Contract_hashes_are_locked_and_consistent}（R0）；Domain.FieldSpecTests.{Evidence_scope_per_operation, Evidence_covers_follows_the_table} | VerifiedScopeScenarios | 完成（R0 清单） |
| I12 AutomationId 与控件名称不变 | UI | Inventory.AutomationIdInventoryTests（R0） | 窗口脚本 | 完成（R0 清单） |
| I13 “明确禁用”的操作仍禁用 | Domain、Infrastructure | Domain.FieldSpecTests.{Jumpseat_identity_and_create_only_fields_are_blocked_on_update, Clearing_decisions_follow_the_table}；IntegrationTests.Golden.Requests.RequestGoldenTests.Update_with_cleared_draft_field_is_blocked_before_any_request（R0）；Domain.FleetAssignmentServiceTests.An_aircraft_has_exactly_one_fleet_and_only_replace_is_allowed | VerifiedScopeScenarios、LiveCompatibilityScenarios（空机队集合、jumpseat 集合、类型转换、改绑、weight_unit、SimBrief） | 待办（R3+） |
| I14 性能不退化 | 基准 | 待办（基准项目 R7） | `--benchmark-core`、PerformanceScenarios；旧 20000 条规划与校验计时断言已改为只验证正确性 | 待办（R3+） |
| I15 EXE 名、数据目录、环境变量、图标 | 打包 | Inventory.ConstantsInventoryTests.Endpoint_data_directory_and_executable_identity_are_locked（R0） | PackageChecks | 待办（R3+） |

## 只在旧测试里出现（或旧测试表达得更多）的行为

| # | 行为 | 位置与处置 |
|---|---|---|
| X01 | 演示执行器 `BatchExecutor`：依赖项成功后才解析 `local:` 引用并写入、失败依赖使子项跳过、父项修复后只续跑子项、取消时待执行项不动、持久化失败停止所有写入、401／403 暂停后不发后续行、已有远端 ID 的恢复项不重复写入 | Program.cs 中约 158–261 行的演示执行器场景。属 Application 层，且部分通过 `persist` 回调间接涉及保存，按任务约定留在旧测试，待 R4 重建 |
| X02 | 执行期间切换语言不重发写入、不改变任务状态，保存后的描述在另一种语言下仍可显示 | Program.cs “Language switch during execution” 场景，走 `BatchExecutor`，留旧；语言和消息描述本身的部分已有 Domain.LocalizationTests |
| X03 | 语言偏好由 `WorkspaceStore` 保存与读取，非法值回落为中文 | 旧场景 354 的存储部分，已拆为独立场景 “Language preference persists in the store…”，留旧，待 R3 |
| X04 | 旧全快照对照的随机历史测试在 29 回合处经 `WorkspaceStore` 保存后重载并继续比对；同格续写时保存的快照不被之后的编辑污染；单格编辑加持久化的 P95 ≤ 100 ms 基准；v0–v3 迁移与损坏 v3 历史回滚；实际 v3 二进制拒读 v4 | HistoryScenarios 中保留的场景，均依赖 SQLite；领域部分（与全快照模型的随机对照、JSON 往返）已在 Domain.EditHistoryTests 覆盖 |
| X05 | `v4 batch history…` 旧场景还会把每个 K 的保留内存字节数和 JSON 字节数写入 `artifacts/step5-performance/batch.json` | 场景已删除，结构断言（只保留 K 行、结构步骤带行序）已迁移；内存字节数这项测量不再自动产生，若还需要应放入 R7 基准项目 |
| X06 | 旧场景 188 对 2 万条航线的规划＋校验加了 10 秒计时断言 | 改为 Domain.SchemaValidationTests.Twenty_thousand_unchanged_routes_validate_and_plan_clean，只验证结果；速度由 `--benchmark-core`（I14）度量，单元测试里不做计时断言 |
| X07 | AuditScenarios、ReauditScenarios、LifecycleScenarios 逐个检查后，没有仅依赖领域层的场景（都使用 `OperationsAdapter`／`OperationsBatchExecutor`／模拟 HTTP／`WorkspaceStore`） | 全部保留，待 R3–R5 |
| X08 | “停用航线”两个入口结束日期不同（工具栏 UTC −2 天，批量规则 −1 天；审查文档 A3）仍未决定 | Domain.RuleEngineTests.Retire_sets_the_end_date_to_yesterday_in_utc_whatever_field_the_rule_names 只固定了规则引擎现在的 −1 天，不代表这就是预期值 |
| X09 | **产品缺口**：`ShiftDays` 对空日期、格式错误日期或非整数天数直接抛出原始 `FormatException`，没有像 A4 的 `ShiftMinutes` 那样给校验消息或按行跳过 | Domain.RuleEngineTests.ShiftDays_reports_unusable_input_as_a_message_instead_of_a_raw_parser_exception，标 `Skip`，待产品修复后启用；R2 不改 `src/` |

## 回归测试对照（CHANGELOG／审查文档登记的已修复领域问题）

| 问题 | 新测试 |
|---|---|
| A1 两个远端机场共用 IATA 使刷新失败 | Domain.SnapshotMergerTests.A1_two_remote_airports_sharing_an_iata_code_do_not_fail_the_refresh_and_the_code_matches_no_local_row；IntegrationTests.Golden.Requests.RequestGoldenTests.Refresh_two_airports_with_the_same_iata（R0，端到端）；旧 UnblockScenarios.RunRefresh 仍在 |
| A2（OperationsBatchExecutor／OperationsAdapter，Infrastructure） | 不属于领域层；对应旧回归仍在 tests/VamSys.Tests，待 R5 |
| A4 平移分钟跨午夜、非法输入 | VamSys.UnitTests.BatchShiftMinutesTests（R1，全部）；Domain.RuleEngineTests.Minute_shift_rows_that_cannot_move_come_back_unchanged_and_are_listed_as_skips |
| A5 草稿行只替换不原地修改 | VamSys.UnitTests.RowReplacementTests（全部，Debug 下行守卫）；本批所有新测试都通过替换行修改数据，在 Debug 与 Release 下都通过 |
| `LOCAL:` 引用大小写 | VamSys.UnitTests.LocalReferenceTests（R1，全部）；Domain.CsvAdapterTests.Import_writes_the_local_reference_prefix_in_lowercase_whatever_the_file_used；Domain.ChangePlannerTests.The_local_prefix_is_recognised_in_any_case_when_resolving_and_when_depending；Domain.CsvAdapterTests.Export_refuses_a_reference_to_a_row_that_has_no_remote_id_yet；IntegrationTests.LocalReferenceCaseTests（R0） |

## 测试有效性：回植错误（R7 做法第 7 步，领域规则部分）

对产品代码临时植入错误，运行单元测试（Debug），确认至少有一条新测试失败，随后 `git checkout -- src` 并确认 `git status src` 干净。

| 规则 | 植入的错误 | 失败的新测试 |
|---|---|---|
| 三方合并冲突检测 | `SnapshotMerger` 不再登记“双方都改且值不同”的冲突 | 6 个用例失败：SnapshotMergerTests.A_field_follows_the_three_way_rule（若干数据行）、An_imported_candidate_matches_the_remote_row_by_key_and_differing_values_need_review、Unresolved_conflicts_survive_another_refresh_for_rows_that_are_still_there |
| 撤销／重做恢复行序 | `EditHistory.Apply` 的结果行序改用来源行序 | 19 个用例失败，含 EditHistoryTests.Undo_restores_the_exact_row_order_and_redo_reapplies_it、Legacy_full_snapshot_histories_convert_to_the_same_undo_and_redo_states、DeletionServiceTests.New_drafts_are_removed_existing_rows_are_marked_and_both_undo_together、CsvAdapterTests.Patch_import_appends_unknown_keys_as_new_rows_and_undo_removes_them_again |
| CSV 引号与转义 | `CsvAdapter.Encode` 不再把单元格内的 `"` 写成 `""` | 5 个用例失败：CsvAdapterTests.Export_quotes_a_cell_only_when_it_has_a_comma_quote_or_line_break、Export_then_parse_returns_the_same_cell、A_planned_export_can_be_read_back_into_equal_rows |
| 规划中 FieldIntent 清空与未指定 | `ChangePlanner.Plan` 把空值也记为 `Set` | 7 个用例失败：ChangePlannerTests.Only_changed_fields_are_planned_and_an_empty_value_is_Clear_not_unspecified、Planning_matches_a_straightforward_full_row_oracle、CsvAdapterTests.Patch_import_keeps_unlisted_rows_and_columns_and_an_explicit_empty_value_clears |
| 本地引用阻止删除 | `DeletionService.Preview` 不再调用 `CheckReferences` | 17 个用例失败：DeletionServiceTests 中的引用检查各用例（别名、airport 端点、过期预览、Info-* 列、同时删除）、SchemaValidationTests 中被引用行删除的用例 |

## R5 传输管线：已删除的旧场景（第 1 项）

删除前逐条读过断言；每个断言在新测试中都有对应。

| 旧场景 | 断言 | 新测试 |
|---|---|---|
| Program.cs “Transport follows cursor pages, protects origin and reports auth” | 两页共 2 行且 2 次请求；其它源被拒；401 抛出 `ApiAccessException` | IntegrationTests.Transport.OperationsTransportTests.{Pages_are_requested_with_one_hundred_rows_and_followed_by_cursor, Reads_and_writes_outside_the_operations_origin_are_refused_before_any_request, A_page_that_fails_with_an_auth_error_surfaces_it}；Transport.PagerTests |
| Program.cs “429 honors Retry-After and retries without losing page” | 首次 429 带 Retry-After，重试后拿到完整页 | OperationsTransportTests.{Get_is_retried_after_throttling_or_a_server_error, Get_retries_wait_the_backoff_and_the_retry_after, Retry_after_server_budget_and_read_backoff_keep_the_production_waits}；RequestBudgetTests.Retry_After_* |
| ContractScenarios “Rejected page size falls back to the documented default” | 422 后第二次请求不带 page[size]，行数 1 | PagerTests.First_page_rejecting_the_size_falls_back…（400、422）；OperationsTransportTests.A_rejected_page_size_falls_back_to_the_default_page |
| PerformanceScenarios “Shared virtual clock enforces request spacing and waiting cancellation” | 两个传输共享预算发 61 次，跨度 ≥60 秒、相邻 ≥1 秒；排队中的请求取消后不再发出 | OperationsTransportTests.{Transports_of_one_va_share_the_budget_and_stay_one_second_apart, A_queued_read_can_be_cancelled_without_sending}；RequestBudgetTests.{Sixty_one_requests…, Instances_for_one_va…, Cancelling_a_queued_request…} |
| PerformanceScenarios “Auto-advancing test clock still applies request spacing and Retry-After” | 自动推进的时钟下间隔 ≥1 秒、429 后 ≥30 秒 | RequestBudgetTests.Instant_time_still_computes_the_waits；OperationsTransportTests.Get_retries_wait_the_backoff_and_the_retry_after |
| PerformanceScenarios “Concurrent clients share one budget and cancel a queued request” | 三个客户端共享预算、间隔 ≥1 秒、排队的取消 | RequestBudgetTests.{Instances_for_one_va…, Requests_of_one_va_never_overlap, Cancelling_a_queued_request…}；OperationsTransportTests.A_write_cancelled_while_queued_behind_the_rate_limit_is_not_sent |
| PerformanceScenarios “Virtual Retry-After, server budget and read backoff preserve production policy” | delta／date／budget／network／500 五种情形：请求 2 次、重试计数 1、等待 ≥5／5／60／1／1 秒、等待指标 ≥1000 ms | OperationsTransportTests.Retry_after_server_budget_and_read_backoff_keep_the_production_waits（同五种情形、同断言）；RequestBudgetTests |
| ApiScenarios “OAuth uses form Client Credentials and actual expiry with cached token” | 令牌地址、表单字段（含 `scope=*`）、第二次取令牌不再请求 | IntegrationTests.Transport.ClientCredentialsTests.{The_token_request_is_a_form_post…, The_token_is_cached_until_one_minute_before_expiry}；Transport.TokenProviderTests |

Program.cs 的 “401/403 pauses batch without sending later rows” 断言的是演示执行器 `BatchExecutor` 的行为（见 X01），不是传输，保留。PerformanceScenarios 的 “Token expiry uses injected time and metrics never expose credentials” 还检查性能指标序列化，保留。

`TransportOutcome` 是带静态成员的 `record struct` 而不是枚举，这样枚举清单（Inventory/Expected/Enums.txt）不变。

## 数量

- R5 第 1 项：旧运行器 240 → 232（删除 8 个传输场景）；单元测试 404 → 576 项，集成测试 50 → 129 项（各含 1 项跳过），Release 与 Debug 均通过；请求金样、存储／CSV 金样和清单期望文件未变；分析器基线 186 → 180（删除的旧场景带走 6 条警告，新测试无新增）。
- 旧运行器：270 项 → 240 项（删除 30 个场景；另有 1 个场景只保留存储部分后改名，数量不变）。基线数在工作树中比主检出少一项（`artifacts/step5-v3-baseline` 缺失），属预期。
- 单元测试：74 项 → 404 项（含 1 项因产品缺口跳过）；Release 与 Debug 均通过。集成测试 50 项（49 通过、1 跳过）未变；金样与清单期望文件未变。
- 分析器警告基线：198 → 186（旧运行器 CA1307 −11、CA1310 −1），新测试没有增加任何警告。
