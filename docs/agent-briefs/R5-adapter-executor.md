# R5：适配器、核对组件与执行流水线拆分

先读 [简报规则与通用约束](README.md)，它们全部适用于本任务。

## 目标

把 `OperationsAdapter`（映射、请求规划、契约校验、回读判定、身份索引混在一起）和 `OperationsBatchExecutor` 的主循环拆成职责单一、可单独测试的组件。线上请求逐字节不变，执行顺序、检查点、恢复和写入安全规则不变。传输层已在 R5 第一部分完成，不在本任务内。

## 工作树与分支

- 工作树：`F:\vCCA\wt-r5-adapter`
- 分支：`r5/adapter`，起点 `7750c70`
- 并行任务：`F:\vCCA\wt-r4-services` 负责 `src/VamSys.Application` 和 `src/VamSys.App`。本任务不得修改这些目录。`OperationsAdapter`、`OperationsBatchExecutor` 等现有的公开或内部接口必须保持可用（可以变成转发到新组件的外观），这样 App 和测试不用跟着改。

## 负责的文件

- `src/VamSys.Infrastructure/` 中 `Transport/` 以外的文件：`OperationsAdapter.cs`、`OperationsBatchExecutor.cs`、`OperationsQueries.cs`、`CandidateIndex.cs`、`OperationsMovement.cs`、`OperationsDeletionVerification.cs`、`OperationsUnknownResolution.cs`、`OperationsServerValue.cs`、`OperationsParameters.cs`、`DemoService.cs`、`ApiContracts.cs`；新组件放在 `src/VamSys.Infrastructure/Api/`。
- `tests/VamSys.UnitTests/Api/`、`tests/VamSys.IntegrationTests/Api/`：新测试。
- `docs/TEST-COVERAGE.md` 中 F15、F16、F17、I01、I02、I04、I07、I11、I13 等 API 层行，以及 `src/VamSys.Infrastructure/README.md`。

## 必读

- [重构计划](../REFACTOR-PLAN-2026-10-20.md)：4.2 节中 `ResourceMapper`、`RequestPlanner`、`ReadbackVerifier`、执行流水线四行，以及“R5 API 层重构”第 2–5 项。
- [API 契约登记](../API-CONTRACTS.md)、[代码优化计划](../OPTIMIZATION-PLAN.md) 中“不能改变的要求”和“明确不做的优化”。
- `src/VamSys.Infrastructure/README.md`。
- 字段规格表 `src/VamSys.Domain/FieldSpec.cs`：映射和规划从它读取，不再自带字段表。

## 做法与约束

1. **映射与规划**：每类资源一个 `ResourceMapper`（JSON → 行）和 `RequestPlanner`（变更 → `ApiStep` 序列）；契约校验独立为 `ContractValidator`。特殊情况（jumpseat、机队参数、飞机移动、`container_ids` 清空、秒数保留等）显式保留在对应资源的类里并写注释。
2. **核对组件**：`ReadbackVerifier`、`DuplicateFinder`（候选查询与 `CandidateIndex`）、`IdentityIndex`（机场别名等）、`DeletionVerifier`、`UnknownResolver`、`MovementVerifier` 各自独立；移动、删除核对和未知恢复改为组合它们。
3. **执行流水线**：主循环拆为预检 → 判重 → 写入 → 回读 → 检查点；条目改用 Id／LocalId 字典查找，快照按位置删除（审查 B7）。检查点仍通过现有的检查点写入器，顺序不变。
4. **缓存**：按行缓存从 `RawApiJson` 解析出的少量字段（别名、端点 ID、原始时间，审查 B8）。行已不可变，可以按行对象缓存。
5. 每类资源和每个核对组件都有单元测试；执行流水线的每一段都有针对失败路径的测试（超时、5xx、429、保存失败、中断后恢复）。被新测试完全覆盖的旧场景（`ApiScenarios`、`ContractScenarios`、`AuditScenarios`、`ReauditScenarios`、`LifecycleScenarios`、`CandidateIndexScenarios`、`VerifiedScopeScenarios`、`UnblockScenarios` 中的部分）可以按规则删除；真实进程终止的恢复场景（`StorageProcessScenarios`）保留。
6. 不改超时、限流、重试和回读策略；不加并行；不用写入响应代替回读。

本任务允许的清单变化：只允许能力键、常量因文件位置移动而产生的路径变化；枚举、持久化结构、资源键不变。

## 完成标准

- `pwsh scripts/check-all.ps1 -Mode Full` 以 `ALL PASSED` 结束；请求金样逐字节不变，请求次数不变。
- `OperationsAdapter.cs` 和 `OperationsBatchExecutor.cs` 只剩外观和组合代码；在结果中给出改动前后的行数。
- 用 `--benchmark-core` 和 `--benchmark-review` 报告 5 万行用例改动前后的分配量（以分配量为准，计时仅供参考）。

## 结果（由子代理填写）

不超过 20 行：做了什么、提交列表、检查结果、关键数字、未完成或有风险的地方。
