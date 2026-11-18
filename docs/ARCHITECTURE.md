# 架构速览

开始一项改动前先读这一页，再只读与任务有关的文件。各层的详细规则见所在项目的 README。

命名空间沿用 `VamSys.Core`（Domain、Application、Presentation）和 `VamSys.Infrastructure`（Storage、Infrastructure），程序集名与项目名一致；只为改名不做大范围修改。

## 依赖方向

```
VamSys.Domain ← VamSys.Application ← VamSys.Presentation
                                   ← VamSys.Storage ← VamSys.Infrastructure

VamSys.App（WinUI，FlightOpsDesk.exe）引用以上全部项目，把端口接到具体实现
```

`tests/VamSys.UnitTests/Domain/ArchitectureBoundaryTests.cs` 强制这一方向：Domain 不做 I/O；Application 和 Presentation 不引用 WinUI、SQLite 或 HttpClient。

## 各项目放什么

| 项目 | 内容 | 先看 |
|---|---|---|
| `src/VamSys.Domain` | 纯业务规则：模型、不可变 `DataRow`、字段规格表（含每个字段的证据范围）、变更规划、三方合并、撤销历史、校验、CSV、`local:` 引用、变更日志、消息与本地化、独立 API 证据 | `FieldSpec.cs`、`ChangePlanner.cs`、`ChangeJournal.cs`、`IndependentApiEvidence.cs` |
| `src/VamSys.Application` | 会话与用例：保存锁、待保存编辑、自动保存、切换工作区、忙碌与对话框状态、批次执行与检查点、审阅计划、提交交接、隐藏列等用户设置；存储和在线访问的端口接口 | `WorkspacePorts.cs`、`OnlinePorts.cs`、`BatchExecutor.cs`、`*Service.cs` |
| `src/VamSys.Presentation` | 视图模型：数据表、单元格（提示、输入、按下判断）、页脚与选择、排序、对话框、导入、任务页 | `DataGridViewModel.cs`、`DataTableViewModel.cs`、`Dialogs.cs` |
| `src/VamSys.Storage` | SQLite v4：连接、迁移、仓储、行序列化、增量保存、检查点、数据目录与维护备份；`WorkspaceStore` 是对外入口 | `WorkspaceStore.cs`、`README.md` |
| `src/VamSys.Infrastructure/Transport` | HTTP：限流预算、令牌、结果分类、只读重试、分页、同源限制、诊断脱敏 | `OperationsTransport.cs`、`ResponseClassifier.cs` |
| `src/VamSys.Infrastructure/Api` | 资源映射与请求规划（`Planning/`）、契约校验、回读核对与服务器改写的等价规则、判重、身份索引、删除／未知／移动核对、快照刷新；`Pipeline/` 是执行流水线的各阶段 | `Planning/RequestPlanner.cs`、`ReadbackVerifier.cs`、`ServerNormalisation.cs`、`Pipeline/ExecutionPipeline.cs` |
| `src/VamSys.Infrastructure` 根目录 | 对外入口 `OperationsAdapter`、`OperationsBatchExecutor`，人工核对入口、演示数据、数据目录（`AppPaths`） | `OperationsAdapter.cs` |
| `src/VamSys.App` | WinUI 窗口、页面和控件（XAML 模板与少量代码拼装）、数据表控件、单元格提示、本地化绑定；`WorkspaceStoreAdapter`、`OnlineGateway`、`DemoEngine` 把端口接到具体实现 | `MainWindow.xaml.cs`、`MainWindow.Data.cs`、`DataTableTemplates.xaml`、`EditableCell.cs` |

## 测试放在哪里

| 位置 | 内容 | 运行 |
|---|---|---|
| `tests/VamSys.UnitTests` | 各层单元测试，以及 `Inventory/` 清单测试（枚举数值、持久化结构、资源键、能力键、AutomationId、覆盖矩阵、回归清单） | `pwsh scripts/check-all.ps1` |
| `tests/VamSys.IntegrationTests` | 金样（请求、存储、CSV）、真实 SQLite、模拟服务器上的 API 与执行流水线、进程终止恢复、打包与工具检查 | 同上；标为 `Slow` 的只在 `-Mode Full` 运行 |
| `tests/VamSys.TestSupport` | 虚拟时钟、模拟 Operations 服务器（`FakeRemote`）、隔离数据目录 | — |
| `tests/VamSys.UiTests` | C# 窗口测试，驱动 QA 包和正式包；显式运行，会接管鼠标键盘 | 见 [窗口测试说明](../tests/VamSys.UiTests/README.md) |
| `tests/VamSys.Benchmarks` | 性能基准，只测量、写 JSON | 手动，见其 README |
| `tests/UiVerification` | 只编译进 QA 包的窗口控制命令 | 随 QA 包构建 |
| `tools/VamSys.QaSeed` | 打包检查、窗口测试数据、契约漂移检查、只读验证、真实写入取证、性能比对 | 见 [QaSeed 说明](../tools/VamSys.QaSeed/README.md) |

需求与测试的对应关系见 [需求覆盖矩阵](TEST-COVERAGE.md)。

## 改动时最容易踩的坑

- **不变规则**：写入不自动重发、写后必须回读、限流预算、判重与冲突检查、保存顺序与版本号、兼容性，见 [代码优化要求](OPTIMIZATION-PLAN.md#不能改变的要求)。
- **证据才能放宽**：解除样本限制、登记服务器改写的等价规则、扩大自动删除核对，都要有真实证据（报告和运行标记写在代码注释里），并配回归测试。见 [已验证 API 开放范围](VERIFIED-API-SCOPE.md) 和 [能力验证矩阵](CAPABILITY-MATRIX.md)。
- **行不可变**：修改数据行用 `With(...)` 或 `ToBuilder()` 生成新行再替换；变更日志按行对象判断变化。
- **金样与清单**：请求、存储、CSV 金样必须逐字节不变；持久化的枚举按数字保存，成员不能重排；资源键只能新增。新增 AutomationId 要登记到清单。
- **界面**：
  - 查询期间锁定表格，但不切换 `IsEnabled`。
  - 任务进度先保存再显示；提交交接在解锁前切到任务页。
  - 每个单元格都绑定提示框会拖慢翻页，所以文本单元格在鼠标第一次停留时才创建提示框。
  - 机队单元格的提示绑定给系统的提示服务，不在悬停时临时创建，否则指针快速扫过多个机队按钮时窗口会崩溃。

  详见 `src/VamSys.App/README.md`。
- **窗口测试**：要先用 `scripts/build.ps1 -Publish` 和 `-Publish -UiVerification` 生成两个包，运行时加 `--explicit only`，否则全部显示为跳过。
- **`dotnet test`**：不要加 `-nologo`，否则 xUnit v3 会报告“Zero tests ran”。
