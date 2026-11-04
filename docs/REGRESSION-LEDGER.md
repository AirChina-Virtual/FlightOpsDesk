# 回归清单（R7 第⑤块）

每个已修复的问题对应哪条测试。来源是[审查与优化计划](REVIEW-AND-PLAN-2026-10-20.md)的问题编号和[更新日志](../CHANGELOG.md)的日期；范围到 2026-09-29，再早各轮（判重、未知写入恢复、API 关联身份、存储优化）的记录在 [docs/history](history) 下，没有逐条列入，那些行为由[需求覆盖矩阵](TEST-COVERAGE.md)和写入安全状态表覆盖。

测试名称的写法与覆盖矩阵相同。`Inventory.RegressionLedgerTests` 读本文件，要求每一行至少写一条测试，写到的每个类和方法都存在。

只改变性能、没有错误行为的优化（如单元格轻量化、审阅后台化的耗时）不在这里，见[性能报告](PERFORMANCE.md)；保存范围这类可判定的结果除外（R33、R34）。

| # | 来源 | 问题 | 测试 |
|---|---|---|---|
| R01 | A1，10-20 | 两个远端机场共用 IATA 时刷新整体失败 | Domain.SnapshotMergerTests.A1_two_remote_*；IntegrationTests.Golden.Requests.RequestGoldenTests.Refresh_two_airports_with_the_same_iata |
| R02 | A2，10-21 | CSV 写成 `LOCAL:` 时执行阶段找不到依赖项 | VamSys.UnitTests.LocalReferenceTests |
| R03 | A4，10-21 | 平移分钟：无效输入抛原始异常，跨午夜静默回绕 | VamSys.UnitTests.BatchShiftMinutesTests |
| R04 | F1，10-20 | 未发出的待执行项无法放弃，卡住整个工作区 | IntegrationTests.Api.DiscardUnsentTests；Application.TaskRecoveryPresentationTests.Discarded_*；UiTests.DialogTests.Discarding_unsent_* |
| R05 | G6，10-20 | 部分未知原因不出现在差异列表里 | Api.ReadbackVerifierTests.Readback_evidence_* |
| R06 | G1，10-20 | 已接受但回读不一致的写入无法解除 | IntegrationTests.Api.AcceptServerValueTests；Application.TaskRecoveryServiceTests.The_server_value_can_be_accepted_* |
| R07 | G2，10-20 | 机场或航线删除在超时／5xx 后无法解除 | IntegrationTests.Api.UnansweredSoftDeleteTests |
| R08 | 10-20 | 写入未发出（限流等待时取消、令牌失败或被拒）却标为未知 | IntegrationTests.Api.UnreachedAndUnknownWriteTests.A_write_that_never_reaches_*；Api.StepWriterTests.A_request_that_never_left_* |
| R09 | 10-20 | 重启后，步骤全为“已准备”的项只读卡住 | IntegrationTests.Api.WriteSafetyTableTests.After_a_run_stops_* |
| R10 | 10-20 | 从未生效的写入没有出路 | Api.UnknownResolverTests.{A_create_that_finds_*, An_update_that_still_reads_*, An_update_whose_record_changed_*} |
| R11 | 10-20 | 旧版两步新建恢复时可能重复创建 | IntegrationTests.Api.WriteReadbackAndRecoveryStageTests.A_legacy_two_step_*；IntegrationTests.Api.UnreachedAndUnknownWriteTests.A_legacy_two_step_* |
| R12 | 10-20 | 没有机队和机场的新 VA 无法连接 | Api.ResourceReaderTests.An_empty_airline_is_found_* |
| R13 | 10-20 | 纯数字 Client ID 把诊断正文和 error_id 里的同样数字替换掉 | IntegrationTests.Api.ApiDiagnosticsTests.A_numeric_client_id_* |
| R14 | 10-20 | 诊断路径按 Client ID 脱敏，删除的 204／404 核对始终失败 | IntegrationTests.Api.ApiDiagnosticsTests.A_client_id_found_in_the_path_* |
| R15 | 10-20 | 提交时保存失败，未写入的任务留在内存里被下次保存写入 | Application.SubmissionServiceTests.{A_failed_save_*, A_save_that_committed_*, Unreadable_storage_*} |
| R16 | 10-20 | 审阅未知项时处理已解决的项，每确认一项重跑一次任务 | Application.UnknownReviewTests.Review_skips_* |
| R17 | 10-20 | 飞机跨机队移动：回读仍是旧机队时误判，不符时重发 | IntegrationTests.Api.AircraftMoveTests |
| R18 | 10-20 | 机场与航线删除缺完整的 204／404 自动核对 | IntegrationTests.Api.SoftDeleteVerificationTests.Airport_scheduled_* |
| R19 | 10-20 | 提交后最后一次渲染抢锁，保存好的任务静默不启动 | Application.SubmissionHandoffTests；UiTests.SubmissionHandoffTests |
| R20 | 10-20 | 货物容量接受负数、小数和溢出 | Domain.SchemaValidationTests.Cargo_capacity_* |
| R21 | 10-20 | jumpseat 回读呼号／班号为 null 被判不一致，恢复时间窗 | Api.ReadbackVerifierTests.{A_created_jumpseat_*, A_recovered_jumpseat_*} |
| R22 | 10-20 | 有未决写入时仍可清空工作区 | Application.MaintenanceServiceTests.Unresolved_writes_* |
| R23 | 10-20 | 数据表复选框深色主题下勾号看不清 | Inventory.WindowRegressionSourceTests.The_row_style_* |
| R24 | 10-19 | 查询切换 IsEnabled 积压事件，关闭后进程迟迟不退出 | Inventory.WindowRegressionSourceTests.Locking_the_table_*；UiTests.ClosingAndHistoryTests.Closing_* |
| R25 | 10-19 | 判断审阅变化的内容键有碰撞风险 | Review.ReviewContentKeyTests |
| R26 | 09-29 | 航线不含 airline_id 导致读取、刷新和创建失败 | Api.ResourceMapperTests.A_route_has_no_airline_* |
| R27 | 09-29 | POST 响应解析失败时丢失远端 ID | Api.StepWriterTests.The_created_id_is_saved_* |
| R28 | 09-29 | 默认页长太小；服务器拒绝页长时没有退回默认值 | Transport.PagerTests.Requests_one_hundred_*；IntegrationTests.Transport.OperationsTransportTests.A_rejected_page_size_* |
| R29 | 09-29 | 写入被 429 限流时没有回到待执行 | IntegrationTests.Api.WriteSafetyTableTests.A_write_is_resent_*；IntegrationTests.Golden.Requests.RequestGoldenTests.Write_429_* |
| R30 | 09-29 | 成本指数等到提交才被服务器拒绝 | Api.ApiBasicsTests.A_cost_index_* |
| R31 | 09-29 | 日期带 7 位小数秒提交 | Api.ApiBasicsTests.A_route_create_sends_iso_* |
| R32 | 09-22 | 关闭时未先冻结，保存失败不能恢复，重复关闭重复保存 | Application.WindowCloseControllerTests；UiTests.ClosingAndHistoryTests.Closing_* |
| R33 | 09-29 | 自动保存写整个工作区；强制结束丢失编辑 | Application.WorkspaceSessionTests.Autosave_*；UiTests.ClosingAndHistoryTests.Autosave_* |
| R34 | 10-19 | 撤销／重做完整保存并重新查询 | UiTests.DataPerformanceTests；UiTests.ClosingAndHistoryTests.Undo_* |
| R35 | 09-29 | 同一单元格连续输入被自动保存拆成多步撤销 | Domain.EditHistoryTests.Typing_in_the_same_cell_*；UiTests.ClosingAndHistoryTests.Undo_* |
| R36 | 09-29 | 同一机场对重复查询；未读完的结果被缓存 | Api.DuplicateFinderTests.{Routes_are_read_per_*, A_pair_that_was_not_read_*} |
| R37 | 09-29 | 一个代码对应多个机场时可以提交 | Api.RequestPlannerTests.An_airport_code_must_* |
| R38 | 10-19 | 撤销栈为空时仍保存 | Application.BatchEditServiceTests.Nothing_to_undo_* |
| R39 | 10-19 | 没有变化的批量操作仍新增撤销步骤 | Application.BatchEditServiceTests.A_rule_that_changes_nothing_* |
| R40 | B2 | 处理冲突时每解决一个就完整保存一次 | Application.ConflictServiceTests；UiTests.ConflictReviewTests |
| R41 | R6 | 页脚的跳页框和每页条数换语言后仍读旧名称 | UiTests.DataFooterTests |

## 回植检查

`scripts/test-mutations.ps1` 把代码复制到 artifacts 下，逐个植入已知错误，确认对应的测试会失败；不改动工作区的代码。R8 验收要求全部被发现。

~~~powershell
./scripts/test-mutations.ps1                  # 不开窗口：写入安全、回读、判重、版本号、陈旧覆盖等
./scripts/test-mutations.ps1 -Set Window      # 每个错误重新发布一次程序并跑窗口测试，占用桌面
./scripts/test-mutations.ps1 -Only stale-overwrite
~~~
