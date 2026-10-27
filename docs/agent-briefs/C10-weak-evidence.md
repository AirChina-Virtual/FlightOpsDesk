# C10：补齐 C2、C3 中证据偏弱的对应关系

先读 [简报规则与通用约束](README.md)、[架构速览](../ARCHITECTURE.md) 和 [清理共用做法](C0-cleanup-common.md)。旧场景已经删除，原文从 git 历史里读，不需要改旧运行器。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c10-weak-evidence`
- 分支：`cleanup/c10-weak-evidence`，起点见派活消息

## 要补的四处

旧代码读法：`git show <提交>^:<路径>`。

1. **未知写入后“重启再读”**：读 `1c12605^:tests/VamSys.Tests/AuditScenarios.cs`，场景 `Unknown recovery survives restart and never writes: <fault>`（约第 29–60 行）。
   - 原场景在写入失败后保存工作区 JSON，再从 JSON 重建工作区和执行器（模拟重启），然后只读恢复。
   - 新测试 `tests/VamSys.IntegrationTests/Api/AuditRegressionTests.cs` 的 9 种故障没有做这一步。
   - 补上：用真实 `WorkspaceStore`（`TempDataDirectory`）保存，再新建一个 store 读回，用新建的执行器恢复。逐条保留原断言，并断言恢复期间写入请求数为 0。
2. **`cancel` 故障**：同一场景的 `cancel` 分支，即写入进行中取消。
   - 断言任务状态为 `Canceled`、该项为 Unknown（或原场景断言的状态），且重启恢复后不重发。
   - 并入第 1 项的数据行即可。
3. **跨航司机场参与恢复**：同文件 `foreach(var mode in new[]{"ambiguous","foreign","missing"})`（约第 156–170 行）的 `foreign` 分支。
   - 新测试只写了 ambiguous 和 missing。
   - 补上 foreign，逐条保留原断言（是否允许推荐恢复 ID、写入次数、状态）。
4. **草稿保存移交**：读 `7b05b3c^:tests/VamSys.Tests/DraftSaveScenarios.cs` 中关于待保存编辑移交（pending edits hand-over）的场景。
   - 逐条列出它的断言，再与 `tests/VamSys.UnitTests/Application/DraftEditsTests.cs` 中现有测试的断言逐条比对。
   - 原场景经过真实 `WorkspaceStore` 行级保存的部分，在 `tests/VamSys.IntegrationTests/Storage/StorageRowSaveTests.cs` 中补写。
   - 已覆盖的，在结果中写出“旧断言 → 新测试中的哪一行断言”。

## 约束

- 不修改 `src/`。发现产品缺陷：只记录，不修；对应测试用 `Skip` 并写明原因。
- 补写的测试必须先在当前代码上通过；每处至少做一次回植验证（临时改错期望值，确认测试失败，然后撤回），在结果里记录。
- 不修改 `docs/TEST-COVERAGE.md`、`docs/analyzer-baseline.txt`；不新增告警（快速档的 new-warnings 一步必须通过，注意 `StringComparison` 和 `TestContext.Current.CancellationToken`）。
- 运行 `pwsh scripts/check-all.ps1`（快速档），提交说明：`补证据：未知写入重启恢复、取消、跨航司机场恢复、草稿保存移交`。
- 约 30–60 步。

## 结果（由子代理填写）
