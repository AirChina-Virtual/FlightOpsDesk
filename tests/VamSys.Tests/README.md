# VamSys.Tests — 检查功能、恢复和运行速度

这个项目会按顺序执行一组自动检查，例如导入 CSV、修改记录或模拟请求超时。每项通过后输出 PASS；发现结果不符合预期时会报错并停止。它是 net10.0 控制台程序，使用下面的 dotnet run 命令运行，不使用 dotnet test。

测试直接调用 Infrastructure 和 Core 中的实际代码。任务页面的数据处理也使用 App/TaskViewModels.cs 原文件，确保检查的是程序真正使用的逻辑，而不是另一份只供测试的版本。

## 运行入口

以下命令均从仓库根目录执行：

~~~powershell
# 运行全部自动检查
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true

# 只检查 v3 保存方式和任务页面的数据更新
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --v3

# 只检查数据保存
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --storage

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
| CandidateIndexScenarios.cs | 检查同名记录是否都被保留、查重结果是否更新，以及随机数据下结果是否正确 |
| LifecycleScenarios.cs | 连续新增、修改、删除或恢复时，检查各步骤是否互相影响 |
| CredentialSaveScenarios.cs | 模拟保存密钥失败，检查是否留下只保存一半的数据 |
| StorageScenarios.cs | 检查任务单独保存、旧数据库升级和版本识别 |
| StorageV3Scenarios.cs | 检查单条保存是否完整、关闭时是否丢草稿，以及记录顺序是否保持 |
| StorageProcessScenarios.cs | 强制结束正在执行的测试程序，再启动它，检查是否重复提交 |
| TaskViewModelScenarios.cs | 检查页面是否只显示已保存结果，以及更新时计数和列表是否正确 |
| PerformanceScenarios.cs | 统计请求次数，并测量大量记录下的查重和处理速度 |
| TestTime.cs | 让测试可以控制时间，减少不必要的真实等待 |

## 怎样检查程序突然退出后的恢复

一个测试进程扮演服务器并记住已完成的操作，另一个进程执行任务。测试会在发送请求、收到 ID、补充修改或保存结果等指定时刻，强制结束执行任务的进程。重新启动后，检查进度有没有丢失，以及已经完成的操作有没有被重复提交。

这里确实会结束并重启进程，但服务器是模拟的，不会修改真实 vaMSYS 数据。--storage-child 参数由测试程序内部使用，正常运行不需要手动填写。

## 怎样检查实际窗口

--seed-task-ui、--seed-storage-ui、--seed-recovery-ui 等参数会生成专门用于检查窗口的测试数据，不要对正常数据目录运行。../../scripts 中的脚本会实际操作窗口，需要可用的 Windows 桌面。模拟保存失败等检查还需要专用的 UI_VERIFICATION 测试版本。

自动测试通过后，仍要查看不同显示缩放比例下的布局、图标、滚动和双语显示，也仍然需要使用真实账号检查 API 操作。

## 如何读取报告

第八轮共有 151 项自动测试通过。之后更换名称和图标时，又检查了打包和实际窗口操作。报告会分别说明每次测试的时间与范围。比较处理速度时，也要看数据量、机器配置和测量方法，不能只看一个耗时数字。

为新问题补测试时，应检查真正可能出错的结果，例如多发了一次请求、重复创建了记录，或保存失败后数据只更新了一半。

[测试报告](../../docs/TEST-REPORT.md) · [性能报告](../../docs/PERFORMANCE.md) · [项目首页](../../README.md)
