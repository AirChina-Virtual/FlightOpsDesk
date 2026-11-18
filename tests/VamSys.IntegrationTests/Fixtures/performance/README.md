# 性能比对用的已归档测量结果

`IntegrationTests.Packaging.PerformanceComparisonTests` 用这些文件检查 `--compare-performance`（I14）。

- `r0/`：R0 基线（2026-10-20，提交 bd6c831），取自本机 `artifacts/r0-baseline` 的 `core.json`、`core-load.json`、`data-perf-*/benchmark.json`、`baseline-50000/baseline.json`。
- `r8/`：R8 的当前版本（2026-11-03），取自 `archive/2026-11-03-r8/measurements.zip` 内的 `artifacts/r8/core*.json`、`artifacts/ui-tests/data-performance-*/benchmark.json`、`artifacts/ui-tests/baseline-*/baseline.json`。

为了缩小体积，复制时删去了比对用不到的内容：逐次样本（工作区读取的 `samples` 保留，它的中位数要用）、分配量数组、逐帧间隔、界面状态（`state`、`last`）、`dataDirectory`、`reboundEditor` 和状态文字。比对读取的数值没有改动。
