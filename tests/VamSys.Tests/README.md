# VamSys.Tests — 检查功能、恢复和运行速度

这个项目会按顺序执行一组自动检查，例如导入 CSV、修改记录或模拟请求超时。每项通过后输出 PASS；发现结果不符合预期时会报错并停止。它是 net10.0 控制台程序，使用下面的 dotnet run 命令运行，不使用 dotnet test。

测试直接调用 Infrastructure 和 Core 中的实际代码。任务页面的数据处理也使用 App/TaskViewModels.cs 原文件，确保检查的是程序真正使用的逻辑，而不是另一份只供测试的版本。

测试引用会为 Infrastructure 启用 `VamSysTestSupport=true`，从 tests/TestSupport 链接存储故障注入和内部访问授权；这些依赖编译到 artifacts/test-build，正式构建不会复用。测试运行器本身仍位于本项目 bin/Release，因此原有 `--no-build` 和子进程恢复入口保持有效。

## 运行入口

以下命令均从仓库根目录执行：

~~~powershell
# 运行全部自动检查
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true

# 只检查 v3 保存方式和任务页面的数据更新
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --v3

# 只检查数据保存
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --storage

# v4 历史随机对照、迁移、并发和性能检查
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --history

# 在隔离源码副本中验证测试能抓住历史错误
./scripts/test-history-mutations.ps1

# 测量确认修改成功后保存一条记录的开销，并写入文件
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --benchmark-v3 artifacts/v3-rebase.json
~~~

部分检查会调用 Windows 的密钥保护功能或启动额外进程，因此完整测试应在 Windows 上运行。测试文件和数据库要放在单独的目录，不能使用用户真正工作的数据。

## 测试文件分工

| 文件 | 验证重点 |
|---|---|
| Program.cs | 决定运行哪些检查，处理命令参数，并为窗口测试准备数据 |
| ApiScenarios.cs | 检查发送给 API 的请求及服务器返回数据的处理 |
| AuditScenarios.cs、ReauditScenarios.cs | 重现以前发现的问题，防止修改代码后再次出现 |
| ContractScenarios.cs | 按官方规范格式检查航线数据、分页、写入限流、字段校验和日期格式 |
| CandidateIndexScenarios.cs | 检查同名记录是否都被保留、查重结果是否更新，以及随机数据下结果是否正确 |
| LifecycleScenarios.cs | 连续新增、修改、删除或恢复时，检查各步骤是否互相影响 |
| CredentialSaveScenarios.cs | 模拟保存密钥失败，检查是否留下只保存一半的数据 |
| StorageScenarios.cs | 检查任务单独保存、旧数据库升级和版本识别 |
| StorageV3Scenarios.cs | 检查单条保存是否完整、关闭时是否丢草稿，以及记录顺序是否保持 |
| StorageProcessScenarios.cs | 强制结束正在执行的测试程序，再启动它，检查是否重复提交；也检查只保存编辑行时被强制结束的情况 |
| DraftSaveScenarios.cs | 编辑单元格后只保存对应行：检查写入范围、重新读取后与内存一致、旧版本号和缺失行的处理，以及保存失败后的交接 |
| HistoryScenarios.cs | 旧全快照独立对照、全部历史迁移、异步载荷、实际写入范围、淘汰分支、字段顺序与规模基准 |
| TaskViewModelScenarios.cs | 检查页面是否只显示已保存结果，以及更新时计数和列表是否正确 |
| PerformanceScenarios.cs | 统计请求次数，并测量大量记录下的查重和处理速度 |
| TestTime.cs | 两种虚拟时钟，让测试不必真实等待（见下节） |
| PackageChecks.cs | 读取正式／QA 发布程序集元数据，检查测试入口、依赖、文件清单和用户文档链接 |

## 测试中的时间

程序每秒最多发送 1 次请求，遇到 429 或服务器错误时还会等待后重试。如果测试使用真实时间，这些等待都会真的发生：整套测试原来约 102 秒，大部分时间都在等待。TestTime.cs 提供两种虚拟时钟，程序照常计算间隔和等待时长，但不需要真实等待：

| 时钟 | 用法 | 适合的测试 |
|---|---|---|
| `InstantTime` | 每次等待时，时钟直接跳到等待结束的时刻，然后立即继续 | 按顺序执行的一般场景，例如恢复核对、判重、创建和回读。用 `InstantTime.Requests()` 作为请求协调器传给 `OperationsTransport` |
| `TestTime` | 时钟只在测试调用 `Drive` 或 `Advance` 时前进 | 需要精确检查间隔、并发共享额度、排队取消的场景 |

新写的 API 测试不要使用默认的共享协调器（系统时钟），否则会重新引入真实等待。"Auto-advancing test clock still applies request spacing and Retry-After"这项测试用来确认 `InstantTime` 下仍会执行间隔和 Retry-After。

每项测试耗时达到 1 秒时，会在 PASS 行末尾显示耗时。全部完成后，会输出总耗时和最慢的 5 项。目前剩下的较慢项目主要是真实进程终止测试和大数据量保存测试，它们的耗时来自实际工作，不是等待。

## 怎样检查程序突然退出后的恢复

一个测试进程扮演服务器并记住已完成的操作，另一个进程执行任务。测试会在发送请求、收到 ID、补充修改或保存结果等指定时刻，强制结束执行任务的进程。重新启动后，检查进度有没有丢失，以及已经完成的操作有没有被重复提交。

这里确实会结束并重启进程，但服务器是模拟的，不会修改真实 vaMSYS 数据。--storage-child 参数由测试程序内部使用，正常运行不需要手动填写。

## 怎样检查实际窗口

--seed-task-ui、--seed-storage-ui、--seed-recovery-ui 等参数会生成专门用于检查窗口的测试数据，不要对正常数据目录运行。`scripts/test-v3-verification-ui.ps1 -Autosave` 会在实际窗口中编辑两次单元格，确认自动保存只写行、没有完整保存，然后强制结束程序，重新读取数据库确认编辑没有丢失。../../scripts 中的脚本会实际操作窗口，需要可用的 Windows 桌面。模拟保存失败等检查还需要通过 `UiVerification=true` 构建的专用测试版本。

使用 `./scripts/build.ps1 -Publish -UiVerification` 生成 QA 版本到 artifacts/v3-qa；窗口辅助代码位于 tests/UiVerification，不编译进正式程序。`./scripts/build.ps1 -Publish` 生成正式包到 artifacts/flightops-app。两种发布均会运行对应的包隔离检查，也可单独执行：

~~~powershell
dotnet run --no-build --project tests/VamSys.Tests -c Release -- --verify-package artifacts/flightops-app
dotnet run --no-build --project tests/VamSys.Tests -c Release -- --verify-qa-package artifacts/v3-qa
~~~

自动测试通过后，仍要查看不同显示缩放比例下的布局、图标、滚动和双语显示，也仍然需要使用真实账号检查 API 操作。

## 如何读取报告

本轮本机共有 192 项自动测试通过，全部运行约 26–28 秒。其中真实 v3 二进制拒绝 v4 的附加用例在 artifacts/step5-v3-baseline 存在时运行；没有该本地基线时为 191 项。`--history` 覆盖 3 个种子各 1,000 次随机操作及固定旧格式夹具；`test-v3-verification-ui.ps1 -History` 使用实际撤销／重做按钮检查跨自动保存、工作区切换和重启。比较速度时也要看数据量、机器配置和测量方法。

为新问题补测试时，应检查真正可能出错的结果，例如多发了一次请求、重复创建了记录，或保存失败后数据只更新了一半。

[测试报告](../../docs/TEST-REPORT.md) · [性能报告](../../docs/PERFORMANCE.md) · [项目首页](../../README.md)
