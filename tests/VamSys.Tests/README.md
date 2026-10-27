# VamSys.Tests — 检查功能、恢复和运行速度

这个项目按顺序执行一组自动检查，例如导入 CSV、修改记录或模拟请求超时。每项通过后输出 PASS；结果不符合预期时报错并停止。它是 net10.0 控制台程序，用下面的 `dotnet run` 命令运行，不使用 `dotnet test`。

测试直接调用 Domain、Application 和 Infrastructure 中的实际代码。不依赖 WinUI 的界面逻辑直接链接 App 中的原文件，包括任务页模型、数据页查询与分页、行／单元格模型、审阅计划和批量预览。这样测试的就是程序真正使用的逻辑。

测试启用 `VamSysTestSupport=true` 引用 Infrastructure，并从 tests/TestSupport 链接存储故障注入和内部访问授权。这些依赖编译到 artifacts/test-build，正式构建不会复用；测试运行器本身仍在本项目的 bin/Release。

## 运行

从仓库根目录执行：

~~~powershell
# 旧运行器全部自动检查（当前 271 项；没有 artifacts/step5-v3-baseline 时为 270 项）
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true

# 只运行其中一部分
dotnet run --project tests/VamSys.Tests -c Release -- --storage            # 数据保存与行级保存

# 基准，写入 JSON
dotnet run --project tests/VamSys.Tests -c Release -- --benchmark-core artifacts/core-performance/current.json
dotnet run --project tests/VamSys.Tests -c Release -- --benchmark-core-load artifacts/core-performance/current-load.json
dotnet run --project tests/VamSys.Tests -c Release -- --benchmark-v3 artifacts/v3-rebase
~~~

剩余的旧场景正按 [TEST-COVERAGE.md](../../docs/TEST-COVERAGE.md) 逐步迁移；日常检查请使用 `scripts/check-all.ps1`。

部分检查会调用 Windows 的密钥保护或启动额外进程，因此完整测试要在 Windows 上运行。测试数据都放在单独目录，不使用用户的真实数据。

R0 起新增的 xUnit 项目独立运行，不计入旧运行器的项数（R2 删除领域层旧场景后，工作树中为 240 项，主检出多一项 v3 二进制检查）：`VamSys.UnitTests` 当前 404 项（含 1 项因产品缺口跳过），`VamSys.IntegrationTests` 当前 50 项。需求与新测试的对应见 [TEST-COVERAGE.md](../../docs/TEST-COVERAGE.md)。单元测试应分别用 Release、Debug 运行，以覆盖行守卫的两种编译配置。

~~~powershell
dotnet run --project tests/VamSys.UnitTests -c Release -p:RestoreLockedMode=true
dotnet run --project tests/VamSys.UnitTests -c Debug -p:RestoreLockedMode=true
dotnet run --project tests/VamSys.IntegrationTests -c Release -p:RestoreLockedMode=true
./scripts/check-analyzer-baseline.ps1
~~~

真实数据库回归只读取隔离副本，需设置 `VAMSYS_REAL_DB_DIR` 和 `VAMSYS_REAL_DB_EXPECTED`，否则该项跳过。2026-10-22 的运行使用 R0 已保存副本的再复制目录，50 项集成测试没有跳过。旧清单保留 R0 的逻辑名称与源码路径，测试仅对明确搬迁的文件做路径映射；新增架构测试另查真实程序集，防止逻辑名称掩盖依赖变化。

## 测试文件分工

| 文件 | 验证重点 |
|---|---|
| Program.cs | 选择要运行的检查，处理命令参数，为窗口测试准备数据 |
| tests/VamSys.IntegrationTests/Fixtures/vcca-weight-20261020.json | 8 份保留的真实 API 响应回放：身份与追踪值脱敏，保留数值、null 和包装结构；仅在测试进程回放 |
| CandidateIndexScenarios.cs | 仅保留基准入口 `CandidateIndexScenarios.Benchmark`（`--benchmark-*` 依赖）；其余检查已迁移到 xUnit 测试 |
| StorageScenarios.cs、StorageV3Scenarios.cs | 只保留窗口脚本和基准入口用的夹具与基准；场景已全部迁移到 `tests/VamSys.IntegrationTests/Storage` |
| PerformanceScenarios.cs | 仅保留基准入口 `PerformanceScenarios.Benchmark`、`RouteBenchmark`（`--benchmark-performance` 依赖）；请求次数与查重速度检查已迁移到 xUnit 测试 |
| CorePerformanceBenchmark.cs | 1 千／2 万／5 万行基准（规划对照、位置索引、局部回放的失败原子性已改由 `VamSys.UnitTests` 的领域测试覆盖） |
| DataUiFixture.cs | `--seed-data-ui` 使用的窗口测试合成数据 |
| TestTime.cs | 两种虚拟时钟（见下节） |
| PackageChecks.cs | 正式／QA 包的测试入口、依赖、文件清单和文档链接 |

## 测试中的时间

程序每秒最多发送 1 次请求，遇到 429 或服务器错误时还会等待重试。TestTime.cs 提供两种虚拟时钟，程序照常计算间隔和等待，但不需要真实等待：

| 时钟 | 用法 | 适合的测试 |
|---|---|---|
| `InstantTime` | 每次等待时，时钟直接跳到等待结束的时刻 | 按顺序执行的一般场景；用 `InstantTime.Requests()` 作为请求协调器传给 `OperationsTransport` |
| `TestTime` | 只在测试调用 `Drive` 或 `Advance` 时前进 | 需要精确检查间隔、并发共享额度和排队取消的场景 |

新写的 API 测试不要使用默认的共享协调器（系统时钟），否则会重新引入真实等待。每项测试耗时达到 1 秒时，PASS 行末尾会显示耗时；全部完成后输出总耗时和最慢的 5 项，整套约 30 秒。

编译服务不可用时，可先 `dotnet build tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nr:false`，再直接运行 `dotnet tests/VamSys.Tests/bin/Release/net10.0/VamSys.Tests.dll`。`--export-diagnostic-replay <JSON路径>` 通过模拟 422/500 生成脱敏诊断与 SHA-256；该入口只在测试运行器，不进入正式包。

## 程序突然退出后的恢复

一个测试进程扮演服务器并记住已完成的操作，另一个进程执行任务。测试会在发送请求、收到 ID、补充修改或保存结果等指定时刻，强制结束执行任务的进程；重新启动后，检查进度有没有丢失，已完成的操作有没有被重复提交。服务器是模拟的，不会修改真实 vaMSYS 数据。这些场景现在是 `tests/VamSys.IntegrationTests/Storage/ProcessTermination` 中的 xUnit 测试（标记 Slow），被结束的进程是 `tests/VamSys.ProcessChild`（参数 `storage`／`draft`），不再经过本运行器。

## 实际窗口检查

窗口脚本位于 ../../scripts，会实际操作窗口，需要可用的 Windows 桌面，并通过 `UiVerification=true` 构建的 QA 版本运行（`./scripts/build.ps1 -Publish -UiVerification` 输出到 artifacts/v3-qa）。所有 `--seed-*` 入口在写入前都会拒绝已有文件的目录；脚本默认在 artifacts 下新建 GUID 目录，只结束自己启动的进程，不调用真实 API。

| 脚本 | 用途 |
|---|---|
| `test-data-performance-ui.ps1 -Rows 20000|50000` | 数据页打开、搜索、分页、编辑框重绑、撤销／重做（只做行级保存、不重新查询），并测量计时 |
| `test-baseline-ui.ps1 -Rows 20000|50000` | 启动、工作区读取与切换、审阅、逐帧滚动、内存；只测量，不设门槛 |
| `test-ui-interactions.ps1 -Rows 20000` | 16 种布局组合、密度、批量范围、预览锁、审阅分组 |
| `test-cell-editing-ui.ps1` | 用真实鼠标和键盘点击、输入、Tab、Esc；自动化读写与只读状态 |
| `test-v3-verification-ui.ps1 [-History|-Autosave|-Tasks]` | 真实撤销／重做按钮、自动保存后强制结束、关闭失败恢复、大任务页 |
| `test-storage-ui.ps1`、`test-recovery-ui.ps1`、`test-credentials-ui.ps1`、`test-settings-save-ui.ps1` | 旧数据库升级、待核对 ID、凭据与设置保存 |

性能、基线、交互、单元格编辑和 v3 验证脚本可以用 `-Executable` 指定要测试的程序；其余脚本使用 artifacts 下的正式包或 QA 包。只有 Windows PowerShell 5.1 时，含中文字面量的脚本（如 `test-localization-ui.ps1`）需要另存为带 BOM 的 UTF-8 才能运行；该脚本目前还会在点击“Apply to drafts”时失败，见 [测试报告](../../docs/TEST-REPORT.md#尚未验证或已知问题)。

单独检查发布包：

~~~powershell
dotnet run --no-build --project tests/VamSys.Tests -c Release -- --verify-package artifacts/flightops-app
dotnet run --no-build --project tests/VamSys.Tests -c Release -- --verify-qa-package artifacts/v3-qa
~~~

自动测试通过后，仍要查看不同显示缩放下的布局、图标、滚动和双语显示，也仍然需要用真实账号检查 API 操作。

[测试报告](../../docs/TEST-REPORT.md) · [性能报告](../../docs/PERFORMANCE.md) · [项目首页](../../README.md)
