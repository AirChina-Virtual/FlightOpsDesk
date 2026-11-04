# 测试

各测试项目的用途、运行方式、窗口检查和测量入口。需求与测试的对应关系见 [需求覆盖矩阵](../docs/TEST-COVERAGE.md)，结果见 [测试报告](../docs/TEST-REPORT.md)。

| 项目 | 内容 |
|---|---|
| `VamSys.UnitTests` | Domain、Application、Presentation、API 与传输组件的单元测试，以及 AutomationId、资源键、主题令牌、覆盖矩阵等清单检查 |
| `VamSys.IntegrationTests` | 真实 SQLite 存储、迁移、故障注入、模拟服务器上的 API 与执行流水线、进程终止恢复、金样 |
| `VamSys.TestSupport` | 虚拟时钟、模拟 Operations 服务器、隔离数据目录、存储与窗口测试数据夹具、旧式 HTTP 桩 |
| `VamSys.ProcessChild` | 进程终止测试中被强制结束的子进程 |
| `VamSys.UiTests` | C# 窗口测试：用 Windows 自带的 UI 自动化客户端驱动 QA 包和正式包；显式运行，见 [说明](VamSys.UiTests/README.md) |
| `VamSys.Benchmarks` | 性能基准（`--benchmark-*`），只测量、写 JSON，见 [说明](VamSys.Benchmarks/README.md) |
| `../tools/VamSys.QaSeed` | 人工查看的数据准备、打包检查与诊断回放，见 [说明](../tools/VamSys.QaSeed/README.md) |
| `UiVerification`、`TestSupport` | 只编译进 QA 包的窗口控制命令，以及只编译进测试构建的存储故障注入与友元授权 |

测试与工具启用 `VamSysTestSupport=true` 引用存储和基础设施，这些依赖编译到 artifacts/test-build，正式构建不会复用。

## 运行

日常检查用 `scripts/check-all.ps1`（Release 构建整个解决方案并运行单元与集成测试）；`-Mode Full` 另跑 Debug 和分析器基线。单独运行：

~~~powershell
dotnet run --project tests/VamSys.UnitTests -c Release -p:RestoreLockedMode=true
dotnet run --project tests/VamSys.UnitTests -c Debug -p:RestoreLockedMode=true
dotnet run --project tests/VamSys.IntegrationTests -c Release -p:RestoreLockedMode=true
./scripts/check-analyzer-baseline.ps1
~~~

单元测试应分别用 Release、Debug 运行，以覆盖两种编译配置。部分检查会调用 Windows 的密钥保护或启动额外进程，因此完整测试要在 Windows 上运行。测试数据都放在单独目录，不使用用户的真实数据。真实数据库回归只读取隔离副本，需设置 `VAMSYS_REAL_DB_DIR` 和 `VAMSYS_REAL_DB_EXPECTED`，否则该项跳过。

## 回归清单与回植

[回归清单](../docs/REGRESSION-LEDGER.md)列出每个已修复问题对应的测试，`Inventory.RegressionLedgerTests` 核对它们都存在。`scripts/test-mutations.ps1` 在隔离的代码副本里逐个植入已知错误，确认测试会失败；R8 验收要求全部被发现。

## 测试中的时间

程序每秒最多发送 1 次请求，遇到 429 或服务器错误时还会等待重试。`VamSys.TestSupport/TestTime.cs` 提供两种虚拟时钟，程序照常计算间隔和等待，但不需要真实等待：

| 时钟 | 用法 | 适合的测试 |
|---|---|---|
| `InstantTime` | 每次等待时，时钟直接跳到等待结束的时刻 | 按顺序执行的一般场景；用 `InstantTime.Requests()` 作为请求协调器传给 `OperationsTransport` |
| `TestTime` | 只在测试调用 `Drive` 或 `Advance` 时前进 | 需要精确检查间隔、并发共享额度和排队取消的场景 |

新写的 API 测试不要使用默认的共享协调器（系统时钟），否则会重新引入真实等待。

## 程序突然退出后的恢复

一个测试进程扮演服务器并记住已完成的操作，另一个进程执行任务。测试会在发送请求、收到 ID、补充修改或保存结果等指定时刻，强制结束执行任务的进程；重新启动后，检查进度有没有丢失，已完成的操作有没有被重复提交。服务器是模拟的，不会修改真实 vaMSYS 数据。这些测试在 `VamSys.IntegrationTests/Storage/ProcessTermination`（标记 Slow），被结束的进程是 `VamSys.ProcessChild`（参数 `storage`／`draft`）。

## 实际窗口检查

窗口检查都是 [C# 窗口测试](VamSys.UiTests/README.md)，用 Windows 自带的 UI 自动化客户端实际操作窗口，需要可用的 Windows 桌面，并使用 `./scripts/build.ps1 -Publish` 生成的正式包和 `-UiVerification` 生成的 QA 包。测试都在 artifacts/ui-tests 下新建数据目录，只结束自己启动的进程，不调用真实 API。原来的 PowerShell 窗口脚本已全部改写并删除，对照表见窗口测试的说明。

`scripts/open-datagrid-qa.ps1` 不是测试：它在测试数据上打开 QA 包，供人工查看主题、高对比度与缩放。

自动测试通过后，仍要查看不同显示缩放下的布局、图标、滚动和双语显示，也仍然需要用真实账号检查 API 操作。

[测试报告](../docs/TEST-REPORT.md) · [性能报告](../docs/PERFORMANCE.md) · [项目首页](../README.md)
