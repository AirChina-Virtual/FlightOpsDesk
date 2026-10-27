# C11：逐条核对 C3 中“按测试名对应”的旧场景

先读 [简报规则与通用约束](README.md)、[架构速览](../ARCHITECTURE.md) 和 [清理共用做法](C0-cleanup-common.md)。

## 背景

C3 删除了 `ApiScenarios`、`ContractScenarios`、`AuditScenarios`、`ReauditScenarios`、`LifecycleScenarios` 中的 55 个旧场景。其中重写的 52 个用例没有问题；但 [C3 结果](C3-api-basics-legacy.md) 中“已有测试覆盖”一节的条目只按测试名对上，没有逐条比对断言。

本简报逐条核对这些条目。旧代码用 `git show 1c12605^:tests/VamSys.Tests/<文件>.cs` 读取。

## 分工

两个子代理并行，各做一半，条目编号对应 C3 结果“已有测试覆盖”中的项目符号顺序：

- **A 半**（工作树 `F:\vCCA\wt-c11a`，分支 `cleanup/c11a`）：第 1–7 条。
  - Frozen schema hash、Airport aliases
  - 20,000 snapshots merge、Three-way refresh
  - Current official requests、Airport addressing／clearing／immutability、API messages
  - Aircraft reading、envelopes
  - Legacy prepared creation persists ID
  - Soft delete 204/404、Online update conflict
  - Write 500 not retried、redaction
- **B 半**（工作树 `F:\vCCA\wt-c11b`，分支 `cleanup/c11b`）：第 8–14 条。
  - Spec-shaped routes、Only routes may omit airline_id
  - Created route ID saved before parse fails
  - Throttled writes
  - Airport duplicate rejects both aliases、resolved local dependency
  - Preflight 500 retryable、Post-write verification errors stay unknown
  - Full refresh atomic、Rotated credentials cannot change the bound VA、Paused task rotates credentials

## 每个旧场景的做法

1. 在旧文件中找到该场景，列出它的每条断言（`Check(...)`），包括写入次数、请求路径、请求体、状态、消息。
2. 在所列的新测试中找对应的断言，写成“旧断言 → 新测试名:行号”。必要时也可以对应到其他新测试。
3. 没有对应的断言，按行为补写到对应新测试所在的文件。补写的测试必须先在当前代码上通过。
   - 断言值与旧场景不同、而新值才是正确行为的（例如资源键改名或文案改为状态码），记录原因，不改回旧值。
4. 对补写的测试做回植验证：临时改错期望值，确认测试失败，然后撤回。

## 约束

- 不修改 `src/`。发现产品缺陷：只记录，不修；对应测试用 `Skip` 并写明原因。
- 只改本半负责条目的测试文件。两半都可能改同一个测试文件，所以把新增测试放在文件末尾，便于合并。
- 不修改 `docs/TEST-COVERAGE.md`、`docs/analyzer-baseline.txt`。
- 不新增告警：快速档的 new-warnings 一步必须通过；注意 `StringComparison` 和 `TestContext.Current.CancellationToken`。
- 运行 `pwsh scripts/check-all.ps1`（快速档），提交说明为 `核对 C3：<A|B> 半旧断言逐条对应`。
- 约 30–60 步。做不完时提交已完成的部分，并在结果中列出剩余条目。

## 结果

为避免两半冲突，结果分别写进 `docs/agent-briefs/C11-results-A.md` 和 `C11-results-B.md`，不改本文件。每条格式：

```
<旧场景名>
  <旧断言> → <新测试名>:<行号>   （或：补写 <新测试名>；或：有意不同，原因…）
回植验证：…
```
