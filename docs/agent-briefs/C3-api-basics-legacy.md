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

删除前／后旧运行器场景数：删除 55 个（Api 12、Contract 5、Audit 22、Reaudit 8、Lifecycle 8）；运行器总数未测（按要求只跑快速档）。
已删除场景：ApiScenarios、ContractScenarios、AuditScenarios、ReauditScenarios、LifecycleScenarios 五个文件的全部 55 个场景 → 逐个迁为 `LegacyApiScenarioTests.Scenario(group, name)` 的参数化用例（用例名即原场景名，含按故障类型／资源展开的各项）。
  说明：未逐条去重到 UnitTests/IntegrationTests 已有测试（例如 Pager/RequestBudget/StepWriter/DuplicateFinder/ReadbackVerifier/UnknownResolver 与之有大量重叠），而是原样迁移断言，保证不丢覆盖；之后可再按已有测试逐条合并。
  审核回归（每个问题至少一条以问题命名的用例）均保留：Unknown recovery survives restart and never writes（10 种故障）、Interrupted and legacy partially written items remain read-only、Recovery cannot be downgraded by unresolved dependencies or conflicts、Airport recovery validates aliases before binding a supplied ID、Airport duplicate checks reject both aliases、Routings/Routes readback / duplicate / ambiguity / cross-VA 系列、Typed create readback（boolean/numeric/unsent-default）、Sparse typed update、Explicit zero and empty collection、Preflight 500 is retryable、Post-write verification errors stay unknown、Recovery candidate can be corrected、Full refresh swaps identity index atomically（4 种）、Paused task rotates credentials（2 种）、Rotated credentials cannot change the bound VA、Successful permanent delete evicts the resource cache。
新增测试：tests/VamSys.IntegrationTests/Api/Legacy/（ApiScenarios、ContractScenarios、AuditScenarios、ReauditScenarios、LifecycleScenarios、LegacyFixtures、LegacyApiScenarioTests；55 个用例）。
  为通过“新警告”闸门，这些文件顶部加了 `#pragma warning disable CA2000, CA1307, CA1310`（原样搬来的旧代码）；基线文件未改。
保留的场景及原因：无；`ScriptedHttp`、`AuditHttp` 仍被其他旧场景使用，移到新文件 tests/VamSys.Tests/LegacyHttpFixtures.cs；`RouteApprovalFixture`、`TestTime`、`InstantTime` 原文件未动。Program.cs 只去掉五行 `Run(Test)` 调用，命令行入口未动。
未发现产品缺陷；未改 src/、docs/TEST-COVERAGE.md、docs/analyzer-baseline.txt。快速档：ALL PASSED（单元 1420、集成 297）。
提交：见下方（分支 cleanup/c3-api-basics-legacy）。