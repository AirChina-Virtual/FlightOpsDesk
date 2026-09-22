# VamSys.Tests — 行为、契约、恢复与性能验证

这是一个 net10.0 控制台场景测试程序，而非使用 dotnet test 发现用例的测试框架项目。Program.cs 组织场景，逐项输出 PASS；断言失败会抛出异常并结束执行。

项目直接引用 Infrastructure，间接使用 Core；同时链接 App/TaskViewModels.cs，使用真实任务视图模型验证已提交状态展示，避免维护另一份测试专用实现。

## 运行入口

以下命令均从仓库根目录执行：

~~~powershell
# 完整行为回归
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true

# v3 存储与任务视图模型专项
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --v3

# 存储专项
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --storage

# v3 重基线性能结果写入指定文件
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -- --benchmark-v3 artifacts/v3-rebase.json
~~~

完整场景包含 Windows 凭据保护与子进程行为，应在 Windows 环境执行。测试输出文件与数据库应使用隔离目录，不能指向用户的真实工作区。

## 测试文件分工

| 文件 | 验证重点 |
|---|---|
| Program.cs | 场景注册、基础业务断言、专项参数和 UI 数据准备入口 |
| ApiScenarios.cs | 资源请求方法、字段转换、响应处理和 API 执行行为 |
| AuditScenarios.cs、ReauditScenarios.cs | 历次审核缺陷的固定复现和回归保护 |
| CandidateIndexScenarios.cs | 同签名多实体、索引更新、未知身份及随机对照 |
| LifecycleScenarios.cs | 创建、修改、删除和恢复之间的生命周期交互 |
| CredentialSaveScenarios.cs | 凭据保存失败与事务一致性 |
| StorageScenarios.cs | 任务拆分保存、迁移、版本和兼容行为 |
| StorageV3Scenarios.cs | v3 资源增量、原子重基线、关闭保护及序号语义 |
| StorageProcessScenarios.cs | 父进程保留模拟远端，终止并重启子进程检查未知写入 |
| TaskViewModelScenarios.cs | 未提交状态不提前呈现、局部更新计数和集合稳定性 |
| PerformanceScenarios.cs | 请求量、索引构建与大数据量处理基准 |
| TestTime.cs | 可控测试时间辅助，减少真实等待对场景的影响 |

## 子进程恢复测试

父测试进程维护模拟远端状态，子进程使用独立 SQLite。测试在写请求、返回 ID、补充 PUT 与重基线事务等明确屏障处终止子进程，重启后检查写请求数量和持久化状态。

这是实际进程中断测试，但远端为模拟服务；不会访问真实 vaMSYS 写接口。内部 --storage-child 参数由父测试驱动，不是正常用户启动入口。

## 窗口测试与种子数据

--seed-task-ui、--seed-storage-ui、--seed-recovery-ui 等参数用于构造隔离窗口测试数据，不能对正常数据目录执行。实际窗口验证脚本位于 ../../scripts，要求 Windows 交互桌面；部分受控故障验证要求独立 UI_VERIFICATION 构建。

行为测试通过不能替代 DPI、图标、滚动和双语布局的实际窗口检查，也不能替代真实实例联调。

## 如何读取报告

第八轮记录为 151 项完整回归通过。品牌更新随后运行了针对性构建和实际窗口检查；各次执行日期与范围分开记录。性能数据包括同机配置、测量方法和适用边界，不应只比较一个耗时数字。

新增缺陷回归应复现可观察的错误结果，例如错误写请求数量、未知记录被重发或事务前后状态不一致，避免仅重复实现步骤。

[测试报告](../../docs/TEST-REPORT.md) · [性能报告](../../docs/PERFORMANCE.md) · [项目首页](../../README.md)
