# C11 结果：B 半（第 8–14 条）

旧代码取自 `git show 1c12605^:tests/VamSys.Tests/<文件>.cs`。共核对 11 个旧场景（Full refresh 4 种模式、Paused task 2 种、resolved local dependency 2 种各算一个场景），约 58 条旧断言：其中约 31 条在已有新测试中有对应，约 27 条在已有测试中找不到，已补写。新增 10 个测试方法（共 17 个用例）；另给 2 个已有的映射测试各加了 1 条断言。未改 `src/`、`docs/TEST-COVERAGE.md`、`docs/analyzer-baseline.txt`。未发现产品缺陷。

行号为补写前的已有测试行号（取自核对时读到的文件）。

## 第 8 条

Spec-shaped routes without airline_id refresh under the connected VA with larger pages
  每个请求带 `page[size]=100` → 补写 ResourceReaderTests.A_refresh_asks_for_pages_of_100_with_reads_only_and_files_the_routes_without_an_airline_under_the_one_it_read（已有 Aircraft_are_read_fleet_by_fleet:51 只覆盖 fleet 与 aircraft 两类请求）
  路由 `Identity.ConnectionId == "7"` → 同上补写（映射层 ResourceMapperTests.A_route_has_no_airline_in_its_record_and_belongs_to_the_connected_airline:146 只覆盖映射，不覆盖刷新）
  路由出发／到达显示 EGLL／EGKK → 同上补写
  `SessionAirlineId == "7"` 且全部是 GET → 同上补写
回植验证：把 100 改成 101，补写测试失败。

Only routes may omit airline_id, and never before the VA is known
  舰队缺 airline_id 被拒 → ResourceMapperTests.Resources_other_than_routes_must_name_their_airline:173
  路由在未知 VA 的工作区被拒 → ResourceMapperTests.A_route_of_a_workspace_without_an_airline_needs_a_connection_first:154（有意不同：旧测试只断言抛出 InvalidOperationException，新测试断言消息码 ApiConnectFirst，更严格；另补了一条工作区 AirlineId 仍为空）
  已知 VA 时路由取得 ConnectionId "7" → ResourceMapperTests.A_route_has_no_airline_in_its_record_and_belongs_to_the_connected_airline:146
  已知 VA 时 `SessionAirlineId == null` → 补写（给上一个测试加了一条断言：记录没有 airline_id，不能告诉会话连接的是哪个 VA）
回植验证：把该断言改成 NotNull，ResourceMapperTests.A_route_has_no_airline_in_its_record_and_belongs_to_the_connected_airline 失败。

## 第 9 条

Created route ID saved before parse fails
  POST 只答 `{"id":9}` 后条目 Unknown、RemoteId 为 9、CreateCompleted → 补写 WriteReadbackAndRecoveryStageTests.A_created_route_whose_answer_cannot_be_read_in_full_keeps_its_id_across_a_restart_and_is_confirmed_without_a_second_post（已有 StepWriterTests.An_answer_with_an_id_but_an_unreadable_record_still_leaves_the_id_saved:178 是舰队，且在步骤层，不经执行器与保存）
  序列化后重启，RemoteId 仍是 9 → 同上补写
  重启后只发 `GET routes/9`，条目 Succeeded，POST 总数仍为 1 → 同上补写
回植验证：把期望的 RemoteId 改成 "8"，补写测试失败。

## 第 10 条

Throttled writes return to Pending and succeed on resume
  创建的 POST 被 429：条目 Pending、码 ApiRateLimited → 补写 WriteReadbackAndRecoveryStageTests.A_rate_limited_create_goes_back_to_pending_with_nothing_created_and_is_posted_again_on_the_next_run（已有 A_rate_limited_write_goes_back_to_pending…:42 与 RequestGoldenTests.Write_429_returns_to_pending_and_is_sent_once_on_resume:333 都是 PUT 更新）
  `!CreateCompleted`、步骤全是 Prepared → 同上补写（另断言 RemoteId 为空）
  恢复后 Succeeded、RemoteId 取得、POST 共 2 次 → 同上补写
  有意不同：RemoteId 为 "90"（模拟服务器统一发号），旧值为 "5"；Retry-After 为 30 秒，旧值为 5 秒，不影响行为。
回植验证：把 POST 总数期望改成 3，补写测试失败。

## 第 11 条

Airport duplicate checks reject both aliases and allow a distinct airport
  EGLL（ICAO）被拒 ApiDuplicate、只有 GET → 补写 PreflightAndDuplicateStageTests.An_airport_create_is_refused_when_its_code_is_either_alias_of_a_known_airport_in_any_case_and_a_distinct_airport_is_created（EGLL 用例）
  小写 lhr（IATA）被拒 → 同上（lhr 用例）；RequestGoldenTests.Create_blocked_when_duplicate_check_finds_same_airport_alias:299 只覆盖大写 LHR，且比对金样
  另一个机场被创建，POST 共 1 次 → 同上（EGSS 用例；有意不同：模拟服务器的标准数据里已经有 LGW，所以改用不存在的 EGSS，行为相同）
  单元层别名匹配 → DuplicateFinderTests.An_airport_is_a_duplicate_when_the_code_is_its_icao_or_its_iata_in_any_case（已有）
回植验证：把 lhr 用例的期望改成非重复，该用例失败。

resolved local dependency completes creation and survives restart（Routings、Routes 各一）
  引用本地机场行的创建完成：Succeeded、POST 1 次 → 补写 PreflightAndDuplicateStageTests.A_route_or_routing_that_points_at_a_created_airport_completes_and_is_not_posted_again_after_a_restart（两种资源）
  草稿行取得远端身份 → 同上补写
  重启（反序列化最后一次保存）后再执行，POST 仍为 1 次 → 同上补写
  已有 A_local_reference_is_replaced_by_the_remote_id_once_the_create_succeeded:180 只覆盖飞机队引用新建机队，未覆盖路线／航路引用机场，也未覆盖重启。
  有意不同：远端 ID 为 "90"（旧值 "9"）；到达机场用 EGCC 以避开标准数据里的重复记录。
回植验证：把 POST 次数期望改成 2，两个用例都失败。

## 第 12 条

Preflight 500 is retryable without inventing an unknown write
  500 后条目 Pending → PreflightAndDuplicateStageTests.A_server_that_cannot_answer_the_preflight_read_leaves_the_item_pending:50（更新，500 与 429）与 A_candidate_read_that_fails_leaves_the_create_pending_and_nothing_is_created:218（创建）
  只发过 GET → 同上两条（Count PUT/POST 为 0），另在补写里逐请求断言全是 GET
  RemoteId 为空、`ApiExpectedValues == null` → 补写 A_create_whose_candidate_read_fails_has_written_nothing_and_after_a_restart_is_posted_exactly_once
  重启后重试：Succeeded、POST 恰好 1 次 → 同上补写
回植验证：把 POST 次数期望改成 2，补写测试失败。

## 第 13 条

Post-write verification errors stay unknown with durable typed expectations
  保存回调执行时条目为 Running 且预期值已写入、且此时还没有 POST → 补写 WriteReadbackAndRecoveryStageTests.A_create_whose_read_back_is_rejected_stays_unknown_with_its_expectations_saved_before_the_post_and_is_confirmed_after_a_restart（断言第一次 POST 前最后一次保存的预期值键为 code、name、parameter_ids、type）。有意不同：旧测试断言 3 个键；新建机队要求带航司参数，所以多一个 parameter_ids，共 4 个键。
  回读答 422 后条目 Unknown、RemoteId 已记录 → 同上补写。已有 A_read_back_that_fails_leaves_the_item_unknown_and_a_later_read_confirms_it_without_a_second_write:162 是更新加 500，不是创建加 422，也没有重启。
  重启后预期值仍在 → 同上补写
  重启后读到记录：Succeeded，POST 仍为 1 → 同上补写
回植验证：把键列表去掉 parameter_ids，补写测试失败。

## 第 14 条

Full refresh swaps identity index atomically（success、read-failure、cancel、save-failure）
  success：用 LHR 规划航路得到新机场 ID 103 → 补写 ResourceReaderTests.A_refresh_swaps_the_airport_index_together_with_the_workspace_or_not_at_all（success 用例）。已有 A_refresh_reads_everything_saves_it_and_only_then_replaces_the_workspace:192 只查数量与保存时机。
  success：保存的暂存工作区与换入后的工作区序列化一致 → 同上补写
  success：旧机场的数字 ID 101 不再可用 → 同上补写
  三种失败：LHR 仍解析成旧 ID 101 → 同上补写（三个用例）
  三种失败：未保存、工作区序列化与刷新前一致 → 同上补写。已有 A_refresh_that_fails_to_read…:218、…fails_to_save…:230、A_cancelled_refresh_changes_nothing:239 只查快照为空与未保存。
  有意不同：读失败用例仍是 TimeoutException，新测试用 ThrowsAny 断言抛出任意异常（旧测试同样只接受 Timeout／Canceled／IO 三类）。
回植验证：把成功时期望的 ID 103 改成 104，success 用例失败。

Rotated credentials cannot change the bound VA
  读到另一航司的记录抛 ApiWrongVa → ResourceMapperTests.A_record_of_another_airline_is_refused:138 与 ResourceReaderTests.Reads_that_need_the_session_airline…:163（映射层）；补写 ResourceReaderTests.Records_of_another_airline_stop_a_read_and_leave_the_bound_airline_alone（走 ReadAllAsync）
  工作区 AirlineId 仍为 "7"、`SessionAirlineId == null`、只有 GET → 补写（同上）
回植验证：把 `SessionAirlineId` 的断言改成 NotNull，补写测试失败。

Paused task rotates credentials and resumes in the same VA（pending、unknown 各一）
  401 后 Job 为 Paused，条目保持 Pending／Unknown → WriteReadbackAndRecoveryStageTests.A_rejected_login_during_the_write_pauses_the_job_and_the_rest_waits:59（Pending）与 AuditRegressionTests.Unknown_recovery_never_writes_whatever_fails…:72（Unknown，401 用例）；补写 CredentialSaveTests.A_task_paused_by_a_rejected_login_survives_a_credentials_rotation_and_resumes_in_the_same_airline 里也断言
  轮换后凭据 ClientId 去空格、密钥原样 → CredentialSaveTests.Both_credential_entries_normalize_the_OAuth_ID_keep_the_secret_exactly_and_roll_back_a_failed_save:34
  任务页重新加载后 AirlineId、LocalRouteTimes、InstanceUrl、任务 JSON 不变 → 补写（同上方法）。已有 Full_settings_stay_blocked_while_a_task_is_open_but_a_credentials_only_save_preserves_it:97 里 LocalRouteTimes 是默认值 false，没有真正验证；InstanceUrl 未断言；任务 JSON 比较的是内存对象而非重新加载的结果。
  重新加载后 Mode 为 Offline → 补写（同上方法，保持旧行为）
  令牌请求带新密钥 `client_secret=new-test-secret` → 补写（同上方法）；CredentialSaveTests 第一条另验证过 client_secret 表单编码
  后续请求带新令牌 `Bearer renewed` → 补写（同上方法）
  重新识别航司，`SessionAirlineId == restored.AirlineId` → 补写（同上方法）
  恢复执行：Pending 只发 1 次 PUT，Unknown 不再发 PUT，最终 Succeeded → 补写（同上方法）
  有意不同：Unknown 条目由真实的“服务器已应用写入、响应丢失”场景产生，旧测试直接设置条目状态；使用共用的模拟服务器与 `WorkspaceStore`。
回植验证：把 `Bearer renewed` 改成 `Bearer renewed2`，两个用例都失败。

## 汇总

新增测试：
- tests/VamSys.UnitTests/Api/ResourceReaderTests.cs（3 个方法，6 个用例）
- tests/VamSys.IntegrationTests/Api/WriteReadbackAndRecoveryStageTests.cs（3 个）
- tests/VamSys.IntegrationTests/Api/PreflightAndDuplicateStageTests.cs（3 个方法，6 个用例）
- tests/VamSys.IntegrationTests/Storage/CredentialSaveTests.cs（1 个方法，2 个用例）
- 辅助：tests/VamSys.IntegrationTests/Api/PipelineHarness.cs 末尾加了 `Restarted`（重启后用新适配器）
- 加强的已有测试：ResourceMapperTests 中 A_route_has_no_airline_in_its_record_and_belongs_to_the_connected_airline、A_route_of_a_workspace_without_an_airline_needs_a_connection_first 各 1 条断言

产品缺陷：无。
未做完的条目：无。
