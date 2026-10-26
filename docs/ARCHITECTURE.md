# 架构速览

给开始某项改动的人（包括子代理）的一页导航：先读这里，再只读与任务有关的文件。各层的详细规则见所在项目的 README。命名空间仍沿用 `VamSys.Core`（Domain、Application）和 `VamSys.Infrastructure`（Storage、Infrastructure），以免只为改名产生大量差异。

## 依赖方向

```
VamSys.Domain  ←  VamSys.Application  ←  VamSys.Storage
                                      ←  VamSys.Infrastructure  ←  VamSys.App（WinUI，FlightOpsDesk.exe）
```

`tests/VamSys.UnitTests/Domain/ArchitectureBoundaryTests.cs` 强制这一方向：Domain 不做 I/O，Application 不引用 WinUI、SQLite 或 HttpClient。

## 各项目放什么

| 项目 | 内容 | 入口文件 |
|---|---|---|
| `src/VamSys.Domain` | 纯业务规则：模型、不可变 `DataRow`、字段规格表、变更规划、三方合并、撤销历史、校验、CSV、`local:` 引用、变更日志、消息与本地化 | `Models.cs`、`DataRow.cs`、`FieldSpec.cs`、`ChangePlanner.cs`、`ChangeJournal.cs` |
| `src/VamSys.Application` | 会话与用例：`WorkspaceSession`（保存锁、待保存编辑、自动保存、切换工作区）、`InteractionGate`（忙碌、查询、对话框、关闭等状态）、各用例服务、`DemoRunner`、审阅计划、提交交接；存储与在线端口接口 | `WorkspaceSession.cs`、`InteractionGate.cs`、`*Service.cs`、`WorkspacePorts.cs`、`OnlinePorts.cs` |
| `src/VamSys.Storage` | SQLite v4：连接、`SchemaV4`、`Migrations/`、`Repositories/`、`RowSerializer`、`UnitOfWork`、增量保存（`WorkspaceWriter`）、检查点、维护备份；`WorkspaceStore` 是对外外观 | `WorkspaceStore.cs`、`WorkspaceWriter.cs`、`README.md` |
| `src/VamSys.Infrastructure/Transport` | HTTP：限流 `RequestBudget`、令牌、结果分类 `ResponseClassifier`、只读重试、分页、同源限制、诊断脱敏 | `OperationsTransport.cs`、`ResponseClassifier.cs` |
| `src/VamSys.Infrastructure/Api` | 资源映射与请求规划、契约校验、回读核对、判重、身份索引、删除／未知／移动核对、快照刷新；`Pipeline/` 是执行流水线各阶段 | `ResourceMapper.cs`、`ReadbackVerifier.cs`、`Pipeline/ExecutionPipeline.cs` |
| `src/VamSys.Infrastructure` 根目录 | 外观：`OperationsAdapter`、`OperationsBatchExecutor`，以及人工核对入口、演示数据、`AppPaths` | `OperationsAdapter.cs` |
| `src/VamSys.App` | WinUI 窗口和页面（目前仍由代码拼装，R6 改为 XAML）、数据表控件、任务页模型、本地化绑定；`WorkspaceStoreAdapter`、`OnlineGateway`、`DemoEngine` 把端口接到具体实现 | `MainWindow.xaml.cs`、`MainWindow.Data.cs`、`EditableCell.cs` |

## 测试放在哪里

| 位置 | 内容 | 运行 |
|---|---|---|
| `tests/VamSys.UnitTests` | 各层单元测试（`Domain/`、`Application/`、`Storage/`、`Transport/`、`Api/`、`Review/`），以及 `Inventory/` 清单测试（枚举数值、持久化结构、资源键、能力键、AutomationId） | `pwsh scripts/check-all.ps1` |
| `tests/VamSys.IntegrationTests` | `Golden/` 金样（请求、存储、CSV）、真实 SQLite、模拟服务器、执行流水线、`RealData/`（只在设置环境变量时运行） | 同上；标记 `Slow` 的只在 `-Mode Full` 运行 |
| `tests/VamSys.TestSupport` | 虚拟时钟、模拟 Operations 服务器、隔离数据目录 | — |
| `tests/VamSys.Tests` | 旧测试运行器，逐步删除中；含真实进程终止恢复场景 | `-Mode Full` |
| `tests/UiVerification`、`scripts/test-*-ui.ps1` | 只编译进 QA 包的窗口控制命令和窗口脚本 | 见 `tests/VamSys.Tests/README.md` |

需求与测试的对应关系见 [TEST-COVERAGE.md](TEST-COVERAGE.md)。

## 改动时最容易踩的坑

- **行不可变**：修改数据行用 `With(...)` 或 `ToBuilder()` 生成新行再替换；变更日志按行对象判断变化。
- **不变规则**：写入不自动重发、写后回读、限流、判重与冲突检查、保存顺序与版本号、兼容性，见 [重构计划](REFACTOR-PLAN-2026-10-20.md) 第 3.2 节。
- **金样与清单**：请求、存储、CSV 金样必须逐字节不变；持久化的枚举按数字保存，成员不能重排；资源键只能新增。
- **界面约束**：查询期间锁表不切换 `IsEnabled`；任务进度先保存再显示；提交交接在解锁前切到任务页。详见 `src/VamSys.App/README.md`。
