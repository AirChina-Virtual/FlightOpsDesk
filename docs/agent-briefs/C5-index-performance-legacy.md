# C5：判重索引与请求次数旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c5-index-performance-legacy`
- 分支：`cleanup/c5-index-performance-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`CandidateIndexScenarios.cs`、`PerformanceScenarios.cs`
- 新测试目录：`tests/VamSys.UnitTests/Api/`、`tests/VamSys.IntegrationTests/Api/`

## 说明

`PerformanceScenarios` 中断言请求次数、查询次数的场景要迁移（请求次数用 `FakeOperationsServer` 计数）；只断言耗时的场景改为不带计时门槛的正确性测试，或在结果中说明为何删除（耗时由 `--benchmark-*` 与窗口脚本衡量）。**保留** `--benchmark-performance` 入口及其依赖的代码。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

删除前／后旧运行器场景数：未运行旧运行器（仅 Quick 档）；按源码删除 29 个场景（CandidateIndexScenarios 10、PerformanceScenarios 19），主会话按 Full 档统计后核对。
已删除场景 → 覆盖它的新测试：
- Planner create-update-create → IndexReservationStageTests.A_create_after_a_rename_is_checked_against_the_new_name_without_reading_the_list_again
- Unknown rename 跨重启保留 → IndexReservationStageTests.An_unknown_rename_keeps_both_names_reserved_across_a_restart_until_it_is_resolved
- Unknown airport aliases → IndexReservationStageTests.An_unknown_airport_create_blocks_every_later_airport_create_whatever_alias_it_uses_and_survives_a_restart
- Unknown recovery faults → IndexReservationStageTests.A_recovery_read_that_fails_in_any_way_never_releases_a_reservation_or_repeats_a_write
- Confirmed index／Signatures／Index building failure／pending reasons → CandidateIndexSessionTests（A_confirmed_rename_or_delete…、Airport_aliases_and_route_endpoints…、A_session_of_one_airline…、A_failed_index_build…、The_pending_reasons…）
- Legacy completed rows → IndexReservationStageTests.A_completed_create_of_an_earlier_version…；CandidateIndexSessionTests.An_unknown_delete_whose_row_has_no_usable_identity…
- Filtered candidates／pair listed once／dense pairs → DuplicateReadCountTests（三页重复、二页 routing 重复、Twenty_route/routing_creates…、A_pair_with_several_pages…）
- pair cache vs confirmed update → DuplicateReadCountTests.A_route_renamed_earlier… / A_routing_changed_earlier…
- Complete indexes → DuplicateReadCountTests.An_airport_create_is_refused…、An_aircraft_registration_is_compared_across_every_fleet…；DuplicateFinderTests
- Index failure not published → DuplicateFinderTests.A_failing_page_publishes_nothing；Successful/unknown creates reserve → DuplicateReadCountTests.A_create_that_succeeded_or_may_have_succeeded…
- Airport dependency／Fleet deletion reads → DependencyReadAndScaleTests（An_airport_has_dependants…、A_route_from_an_airport_to_itself…、A_fleet_delete_only_reads…）
- Airport lookups 3000/1000、Full refresh、Merge 20000 冲突、20k 索引查询 → DependencyReadAndScaleTests 同名行为测试（去掉计时）
- Token expiry → ClientCredentialsTests.The_token_is_cached…（已有）；指标不含凭据 → PerformanceMetricsTests；Save metrics → SaveMetricsTests
- 20,000 routes/100 creates 单次过滤 GET → DuplicateReadCountTests.One_hundred_route_creates…
- Seeded random route oracle：未逐项迁移，由 pair 缓存测试与 DuplicateFinderTests 的确定性用例覆盖（取舍，主会话可复核）。
新增测试：UnitTests/Api/CandidateIndexSessionTests.cs（5）、DependencyReadAndScaleTests.cs（含 PerformanceMetricsTests，约 15）；IntegrationTests/Api/IndexReservationStageTests.cs（约 15）、DuplicateReadCountTests.cs（11）、SaveMetricsTests.cs（1）。
保留：PerformanceScenarios.Benchmark／RouteBenchmark 与 CandidateIndexScenarios.Benchmark（--benchmark-performance 依赖）；去掉 --performance、--indexes 入口（Run 已删，docs/history 仅历史记录）。
更正：此前 401/403 得到 NeedsAttention 是测试缺陷（被恢复的记录 airports/90 未注册，拦截器没被调用，实际收到 404）；注册该行后 401/403 均为 Paused，空响应体同样 Paused，无产品差异。
提交：见 git log（Quick 档 ALL PASSED：单元 1441、集成 271）。