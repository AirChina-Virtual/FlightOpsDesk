# C7：10 月兼容性旧场景

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c7-live-compatibility-legacy`
- 分支：`cleanup/c7-live-compatibility-legacy`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`LiveCompatibilityScenarios.cs`、`RouteApprovalFixture.cs`、`Fixtures/vcca-weight-20261020.json`
- 新测试目录：`tests/VamSys.IntegrationTests/Api/`、`tests/VamSys.IntegrationTests/Fixtures/`

## 说明

真实响应回放 `Fixtures/vcca-weight-20261020.json` 是保留素材：移到 `tests/VamSys.IntegrationTests/Fixtures/`，由新测试读取。**保留** `--export-diagnostic-replay` 入口：若它依赖本文件的代码，把所需部分移到一个小文件中保留。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

删除前／后旧运行器场景数：未运行旧运行器（只跑快速档）；本文件共删除 14 个场景（12 个 Live + 2 个 Maintenance），`--live-compatibility` 入口随之去掉。
已删除场景（括号内为覆盖它的新测试，均在 tests/VamSys.IntegrationTests/Api/ 或已有测试）：
- kg capacities → LiveCompatibilityTests（单次 POST、Freight 非法值、0、CSV 保留 1.5、稀疏更新保留 raw）；ContractValidatorTests.Properties_the_program_never_sends_are_refused…；SchemaValidationTests.Cargo_capacity_must_be_whole_kilograms_when_online
- diagnostics → ApiDiagnosticsTests（422 全字段/trace/脱敏、1 MB 截断+哈希、非 JSON 与 JSON 内回显的头脱敏、导出不改已脱敏正文哈希、回放 8 例 Theory）
- unknown write → LiveCompatibilityTests.Recovery_never_resends_and_keeps_the_original_write_error…；WriteReadbackAndRecoveryStageTests.*
- scheduled fleet sets → LiveCompatibilityTests.Scheduled_fleet_sets_go_from_one_fleet_to_two…（字符串 ID 回读、VerifiedOperations 细分键、清空 ApiClear）
- parameter approval / readback mismatch / cross-VA → LiveCompatibilityTests.A_fleet_set_without_approval…（3 种）、A_scheduled_readback_with_a_missing_extra_or_repeated_fleet…（3 种）；AirlineParametersTests
- new fleets require parameter / string request IDs → LiveCompatibilityTests.A_planned_fleet_has_to_name_a_parameter…、Existing_fleets_cannot_be_given_parameters…；RequestPlannerTests
- capability gate → LiveCompatibilityTests.A_generic_route_update_verification_does_not_lift…
- jumpseat create / unknown POST → LiveCompatibilityTests（身份、显式 null 回读、错误身份、4 种恢复）
- timestamp metadata → LiveCompatibilityTests.A_changed_updated_at_alone_is_not_a_conflict…
- Maintenance ×2 → MaintenanceBackupTests（备份清理、3 个注入点、过期版本、4 种不安全状态、并发编辑）
新增测试：LiveCompatibilityTests.cs（28 例含 Theory 展开）、ApiDiagnosticsTests.cs（12 例含回放）、MaintenanceBackupTests.cs（10 例）；Fixtures/vcca-weight-20261020.json 已移到 IntegrationTests/Fixtures 并在 csproj 复制到输出。新测试里需要先登记记录才能触到的故障（如 jumpseat 丢失应答后的 routes/90）均断言了读取路径确实发生（GET routes/90、被篡改的回读被服务过等）。
保留的场景及原因：RouteApprovalFixture.cs 仍被 PerformanceScenarios.cs 引用，未动；`--export-diagnostic-replay` 入口保留，所需代码移到 tests/VamSys.Tests/DiagnosticReplayExport.cs（输出结构略简化：一个 500 的 Unknown 项＋一个 422 的 Failed 项）。
注意：tests/VamSys.Tests/README.md 仍提到 `--live-compatibility` 和旧夹具位置，因不在本简报范围未改，合并时请更新。
检查：pwsh scripts/check-all.ps1 快速档 ALL PASSED（单元 1455、集成 359）；未运行完整档。
提交：见 git log（清理：LiveCompatibilityScenarios.cs 的旧场景迁移到新测试并删除）