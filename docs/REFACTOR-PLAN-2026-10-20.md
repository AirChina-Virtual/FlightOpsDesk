# 彻底重构计划（2026-10-20）

本文规划 FlightOps Desk 的整体重构：目标架构一次定清，然后分阶段、逐块替换。它以 [全面代码审查与优化计划](REVIEW-AND-PLAN-2026-10-20.md) 的发现为输入，并继续遵守 [代码优化计划](OPTIMIZATION-PLAN.md) 中“不能改变的要求”和“明确不做的优化”。

**范围：** 产品代码的每一层（领域、存储、API、应用、界面，包括数据表控件）和测试体系（自动测试与窗口测试）都要重构，没有哪一部分只是“原样搬家”。

**结论：** 彻底，但渐进。不推倒重写，而是先建立能锁定现有行为的测试安全网，再按“规范 → 领域 → 存储 → 应用 → API → 界面 → 测试体系”的顺序替换结构。测试不逐项迁移旧场景，而是按第 3 节的功能与规则、针对重构后的结构重新设计。重构期间的行为保护由 R0 建立的金样和清单测试承担（它们只看输入输出，不依赖内部结构）；旧测试只作为过渡期的额外保护继续运行，某一层的新测试覆盖完对应需求后，就删除这一层的旧测试。每个提交都能构建、全部回归通过、可以发布；存储格式保持 v4，任何阶段都可以退回上一版程序继续使用同一个数据库。

**为什么不重写：** 写入安全规则（不重发、回读、检查点顺序、未知项只读核对）、v4 迁移、真实 API 证据和 271 项回归的大量细节只存在于现有代码中；用户本机还有需要续跑的 Pending 任务。重写会在“看起来一样”的地方丢失这些细节，而且在全部完成前无法交付任何东西。

## 1. 重构的定义与边界

- **重构只改结构，不改行为。** 判断标准见第 3 节的不变清单。
- **行为修复不混进重构提交。** 审查中的 A2–A4、E、F、G 系列是行为变化，单独提交、单独补失败测试；排期见第 6 节。重构中发现的新问题先登记，不顺手修。
- **不在本计划中做：** 新功能；修改 API 策略（限流、重试、回读）；修改 CSV 格式；修改存储格式 v4；修改 EXE 名、数据目录、`VAMSYS_DATA_DIR`、能力键字符串和契约文件。

## 2. 现状基线

| 项 | 现状 |
|---|---|
| 代码规模 | Core 约 1,300 行、Infrastructure 约 2,300 行、App 约 2,700 行；大量 150–540 字符的单行，多条语句写在一行 |
| 界面 | `MainWindow` 15 个分部文件，页面全部用代码拼装（`Stack`、`Text`、`Button` 辅助方法），页面、状态、保存策略和业务逻辑混在一起 |
| 交互状态 | `busy`、`localOperation`、`querying`、`interactiveOperations`、`dialogs`、`closeState`、`storageBlocked` 等多个标志分散判断 |
| 持久化 | 除单元格编辑和撤销外，几乎所有改动都完整保存（5 万行约 2 s，在界面线程序列化）；“改后立即完整保存”只是约定 |
| API 层 | `OperationsAdapter`（约 540 行）同时负责映射、请求规划、回读判定和身份索引；执行器主循环集中在一个方法 |
| 字段规格 | 分散在 `Schemas`、`OperationsAdapter.Maps`、`Validate`、`IndependentApiEvidence`、`Equivalent` 等至少 6 处 |
| 测试 | 自研控制台运行器，271 项通过，约 30 s；无窗口逻辑以“链接源文件”方式编进测试项目；11 个窗口脚本 |
| 工程 | 无 CI、无 `.editorconfig` 和分析器；本机 shell 中没有 `git` |

## 3. 必须保留的功能与需求

这是重构的验收硬标准，也是新测试的设计依据（R7 的需求覆盖矩阵按这里的编号建立）。表中“现有证据”只说明重构前由哪些旧测试保护，这些旧测试会随重构逐层删除；R0 先补齐目前没有证据的项。

### 3.1 功能清单

| # | 功能 | 现有证据 |
|---|---|---|
| F01 | 多工作区，一个工作区绑定一个 VA；不能改绑到另一家 | StorageScenarios、ApiScenarios |
| F02 | 连接设置：Client ID 去首尾空格、Secret 不裁剪；“保存连接设置”留空保留原 Secret；“仅更新 API 凭据”；DPAPI 加密；保存失败不半更新 | CredentialSaveScenarios、test-credentials-ui、test-settings-save-ui |
| F03 | 测试连接并识别 VA：机队 → 机场 → ranks → containers；读不到时不猜测 | ApiScenarios |
| F04 | 从 API 原子刷新五类资源（飞机逐机队读取）；三方合并、冲突审阅、远端删除不自动重建、仅 `updated_at` 变化不算冲突；刷新后清除对应撤销历史 | ApiScenarios、ContractScenarios、LifecycleScenarios |
| F05 | 数据页：全量搜索／筛选／排序，“条件未应用”提示，分页 100／200／500，跨页选择，舒适／紧凑密度（全局、重启保留），查询可取消 | DataTableScenarios、test-data-performance-ui、test-ui-interactions |
| F06 | 单元格编辑：平时显示文本、点击或 Tab 才建输入框；Enter／Esc／Tab 行为；已删除行只读；700 ms 自动保存只写被编辑行 | DraftSaveScenarios、test-cell-editing-ui |
| F07 | 撤销／重做（最多 20 步，同格连续输入合并）；只替换行时行级保存、局部刷新 | HistoryScenarios、test-v3-verification-ui |
| F08 | 批量操作只作用于已选：批量规则、分配机型、删除、恢复、到期停用；预览显示已选／跳过／未变化／实际变化；预览期间锁定 | UiInteractionScenarios、test-ui-interactions |
| F09 | 机型选择器：单选、多选，scheduled 航线替换／追加／移除，最终集合非空；飞机跨机队移动单独提交 | VerifiedScopeScenarios、LiveCompatibilityScenarios |
| F10 | 新机队选择一个已批准、未受限的航司参数；参数只读加载 | LiveCompatibilityScenarios |
| F11 | CSV 导入：生成待检查修改，不覆盖快照；表头映射；保留未知列；三态字段（不改／设值／清空） | ApiScenarios、AuditScenarios |
| F12 | CSV 导出：按资源、只含变化记录、保留未知列、超过 10 MB 拆分、任务标为“待到 Orwell 导入”；字节格式不变 | AuditScenarios |
| F13 | 工作区审阅：后台构建、按资源分组、本次提交／暂缓／受阻、样本限制与已验证范围、依赖暂缓；提交前后台重建并比较内容键 | UiInteractionScenarios |
| F14 | 提交交接：确认清单（删除另确认）→ 保存 Pending → 解锁前切到任务中心 → 只启动本次任务；不自动续跑历史 Pending | TaskSubmissionScenarios |
| F15 | 在线执行：判重、写前冲突检查、写后回读、检查点、未知项只读核对、401／403 暂停、429 回到待执行、两步新建续传 | ApiScenarios、StorageProcessScenarios、ReauditScenarios |
| F16 | 未知项出路：填写 Remote ID、API 核对未生效、接受服务器当前值、删除人工确认（含无回执 404）、软删除未生效确认、放弃未发出的项 | UnblockScenarios、VerifiedScopeScenarios |
| F17 | 删除：本地引用检查；软删除 204／404 自动核对；“API 只读核对删除”补核对 | VerifiedScopeScenarios |
| F18 | 任务中心：虚拟化卡片、只显示已保存结果、50 ms 合并呈现、已放弃单独计数 | TaskViewModelScenarios、test-v3-verification-ui |
| F19 | 诊断导出：脱敏 JSON＋SHA-256，1 MiB 截断标记，不含凭据 | LiveCompatibilityScenarios |
| F20 | 维护：先 SQLite 备份（含 WAL、integrity_check、清单）再事务清理当前工作区；未决写入时阻止 | LiveCompatibilityScenarios |
| F21 | 演示工作区与离线模式；演示不修改真实数据，连接失败不偷偷切换到演示 | ApiScenarios |
| F22 | 中英文即时切换，不重建页面、保留选择和滚动；读屏控件名称；缺失键记录日志 | test-localization-ui、本地化测试 |
| F23 | 存储：旧整块／v2／v3 迁移到 v4（先备份、全部或全不）、拒绝更高版本、版本号防陈旧覆盖、单数据目录单实例 | StorageScenarios、StorageV3Scenarios、HistoryScenarios |
| F24 | 安全关闭：冻结 → 等待自动保存 → 保存；失败保持窗口打开并允许重试；运行任务或对话框打开时拒绝 | test-v3-verification-ui、关闭保存的自动测试 |
| F25 | 在线任务性能统计写入 `performance/<工作区>/<任务>/<执行>.json` | PerformanceScenarios |
| F26 | 正式包与 QA 包：正式包无测试入口、故障注入、PDB；QA 包含窗口控制命令；包内文件清单检查 | PackageChecks |

### 3.2 不变规则

| # | 规则 | 怎样证明 |
|---|---|---|
| I01 | 写入不自动重发；未知只用 GET 核对；已有 ID 不再 POST | 进程终止测试、请求序列金样 |
| I02 | 写后回读比对，通过才成功并登记已验证操作 | ApiScenarios、请求序列金样 |
| I03 | 进程内所有 VA 合计每分钟 ≤60 次、间隔 ≥1 s、遵守 Retry-After；不并行 | ContractScenarios（TestTime） |
| I04 | 新建前判重，修改前读远端最新，删除前检查引用；判重索引只在完整读取后使用 | CandidateIndexScenarios |
| I05 | 请求发出前、收到响应后先保存；版本号防覆盖；保存失败不改内存 | StorageProcessScenarios、DraftSaveScenarios |
| I06 | CSV 字节格式不变 | CSV 金样 |
| I07 | 请求体逐字节不变（方法、路径、字段、顺序、数字与字符串类型） | **新增**请求金样 |
| I08 | 数据库仍为 v4，表结构、行 JSON 字段名和取值不变，旧版本程序仍能打开重构后写过的库 | **新增**存储金样 |
| I09 | 持久化的枚举按数字保存（未使用 `JsonStringEnumConverter`）：`ItemState`、`ChangeKind`、`RunMode`、`ResourceKind`、`JobStatus` 等成员**不能重排、删除或插入到中间** | **新增**枚举值清单测试 |
| I10 | `Text_…` 等资源键、`MessageDescriptor` 代码和参数名不变（任务中保存了它们） | **新增**资源键清单测试 |
| I11 | 能力键字符串、契约哈希、外部证据基线不变 | VerifiedScopeScenarios |
| I12 | 窗口脚本依赖的 AutomationId 和控件名称不变 | **新增** AutomationId 清单＋窗口脚本 |
| I13 | “明确禁用”的操作仍然禁用（空机队集合、jumpseat 集合、类型转换、既有机队改绑、未验证清空、weight_unit、SimBrief 编辑） | VerifiedScopeScenarios、LiveCompatibilityScenarios |
| I14 | 性能不退化：同机测量的数据页、启动、审阅、核心基准，比基线慢不超过 10% | 性能脚本与 `--benchmark-core` |
| I15 | EXE 名 `FlightOpsDesk.exe`、默认数据目录 `%LOCALAPPDATA%\VamSysBatch`、环境变量 `VAMSYS_DATA_DIR`、图标与品牌不变 | PackageChecks |

## 4. 目标架构

### 4.1 项目与依赖

```
VamSys.Domain         纯业务：模型、字段规格表、规划、合并、撤销历史、校验、CSV
   ↑
VamSys.Application    工作区会话、变更日志、各用例服务、交互闸门；只定义端口接口
   ↑            ↑
VamSys.Api       VamSys.Storage     实现端口：Operations API／SQLite v4
   ↑            ↑
VamSys.Presentation   各页面 ViewModel，不依赖 WinUI（测试直接引用）
   ↑
VamSys.App            WinUI 外壳、XAML 页面、数据表控件、QA 扩展点
```

- 依赖只能向上；Domain 不引用任何其他项目，Application 不引用 SQLite 或 HttpClient。
- 用项目边界替代现在“把 App 的源文件链接进测试项目”的做法。
- 项目沿用 `VamSys.` 前缀（理由同 App README：避免只为改名产生大量差异），见决定 R-D2。

### 4.2 关键组件

| 组件 | 职责 | 取代 |
|---|---|---|
| `FieldSpec` 表 | 每个字段的 CSV 名、线上名、类型、是否可清空、集合比较、证据范围 | `Schemas`、`Maps`、`Validate`、`Equivalent` 等分散定义（C4） |
| `LocalReference` | `local:` 令牌的唯一解析入口 | 各处 `StartsWith("local:")`（为 A2 修复铺路） |
| `ChangeJournal` | 记录被替换／新增／删除的行、顺序、列、冲突、任务和头部变化；存储据此增量写入，为空就不写 | “改后立即完整保存”的约定（C1） |
| `WorkspaceSession` | 持有当前工作区、保存锁、变更日志和保存策略；所有修改都经过它 | `MainWindow.Save`／`SaveEdits` 和散落的保存调用（C2） |
| `InteractionGate` | 单一状态机：空闲、查询中、本地操作、执行中、对话框、关闭中、存储受阻；页面只读状态 | 7 个分散的布尔标志与重复条件（A7） |
| 用例服务 | `ConnectService`、`RefreshService`、`ImportExportService`、`BatchEditService`、`ReviewService`、`SubmissionService`、`TaskRecoveryService`、`MaintenanceService`、`DiagnosticsService`、`DemoRunner` | `MainWindow.*.cs` 中的业务逻辑 |
| `IWorkspaceRepository` | 加载、增量保存、头部保存、定向查询（`JobExists`、`LoadJob`、计数）、检查点 | `WorkspaceStore` 的完整读写（B3–B5） |
| `ResourceMapper` | 每类资源一个：JSON → 行 | `OperationsAdapter.FromJson` |
| `RequestPlanner` | 每类资源一个：变更 → `ApiStep` 序列 | `OperationsAdapter.Plan` |
| `ReadbackVerifier` | 回读判定和差异证据（G6 已合并为一个函数，这里只搬家） | `OperationsAdapter` 内的回读部分 |
| 执行流水线 | 预检 → 判重 → 写入 → 回读 → 检查点，每段独立可测；条目按 Id／LocalId 字典查找 | `OperationsBatchExecutor.ExecuteCoreAsync`（B7） |
| 页面 ViewModel | 每个页面一个，持有页面状态和命令，调用用例服务 | `MainWindow` 中的页面状态 |
| XAML 页面 | 工作区、导入、审阅、任务、设置、数据页全部用 XAML＋`x:Bind` | 代码拼装的页面 |
| `DataGrid` 组件 | 数据表独立成可复用控件：列定义、行虚拟化、单元格编辑、选择、键盘导航、密度，有自己的 ViewModel 接口 | `MainWindow.Data*.cs`、`EditableCell`、`DataRowPresenter`、模板选择器 |
| 样式与主题资源 | 颜色、间距、字号、行高、按钮和单元格样式集中在 `ResourceDictionary`，明暗与高对比度主题统一 | 代码中硬写的尺寸、颜色和样式 |
| `AppPaths`、日志 | 数据目录唯一来源；日志按大小轮换 | 5 处重复拼写（C5）、无限增长的日志（A6） |

### 4.3 重构后的界面仍须满足的约束

界面代码全部重写，但以下来自性能和稳定性问题的约束必须在新结构中保留，并尽量由测试或控件设计强制：

- 查询期间锁表用 `LockDataTable`，不切换 `IsEnabled`。
- 单元格回调经 `RowLink` 读取当前工作区和资源，不直接捕获。
- 行容器回收时保留单元格控件，只更换数据；重新绑定不得触发编辑。
- 本地化注册保存在附加依赖属性中，服务只持有弱引用。
- 任务进度先保存成功再显示；50 ms 合并只影响显示，不推迟保存。
- 提交交接在解锁前明确切到任务页，不能只设 `Navigation.SelectedItem`。
- QA 窗口控制只在 `UiVerification=true` 时编译进来；改为注入接口后，正式包仍不能含任何测试入口。

## 5. 分阶段计划

每个阶段都在独立分支上进行，阶段内小步提交，每次提交都通过全部回归。阶段结束时生成 QA 包，跑该阶段指定的窗口脚本，同机记录性能数字，然后打标签。

| 阶段 | 内容 | 规模 | 依赖 |
|---|---|---|---|
| R0 | 安全网：git、CI、新测试框架、金样测试、清单测试、性能基线 | 中 | 无，**必须最先** |
| R1 | 机械格式化、`.editorconfig`、分析器 | 小 | R0 |
| R2 | 领域层：字段规格表、`LocalReference`、只替换行约束、静态集合 | 中 | R1 |
| R3 | 存储层重构：仓储与工作单元、变更日志、增量保存、迁移模块、后台读写 | 大 | R2，决定 R-D5 |
| R4 | 应用层：`WorkspaceSession`、`InteractionGate`、用例服务、`DemoRunner` | 大 | R3 |
| R5 | API 层重构：传输管线、分页、令牌、限流，适配器和执行流水线 | 大 | R2（可与 R4 并行，但不要同时改执行器） |
| R6 | 界面重构：Presentation 项目、外壳导航、样式资源、XAML 页面、数据表控件 | 很大 | R4 |
| R7 | 测试体系重建：按需求为新结构重新设计测试、窗口测试改为 C#、删除旧测试 | 大 | R0 起逐步进行，R6 后收尾 |
| R8 | 全面验收、文档、发布 | 中 | 全部 |

R7 不是排在最后才开始：R0 引入新框架后，每个阶段重构完哪一层，就为那一层按需求重新编写测试，然后删除这一层的旧测试；R7 负责窗口测试、覆盖核对和删除旧运行器。

### R0 安全网

目标是让“行为不变”可以被机器判断，而不是靠人读差异。

1. **环境。** 安装 Git for Windows（或放入 `.tools`），确认能在本 shell 中提交；建立 `refactor/r0` 等分支。在 Windows 运行器上建立 CI：全部回归＋正式包检查（D2）。
2. **新测试框架。** 新建 `tests/VamSys.UnitTests`、`tests/VamSys.IntegrationTests`（见 R7 的目录规划），使用 xUnit v3（测试项目本身就是可执行程序，适合现有的子进程恢复测试；见决定 R-D4）。把 `TestTime`、`InstantTime`、模拟服务器、隔离数据目录做成共享的测试支持库。CI 同时运行新旧两套。下面第 3–6 步的新测试都写在新框架里。
3. **请求金样（I07）。** 对每类资源 × 新建／修改／删除 × 主要变体（scheduled／jumpseat、机队参数、飞机移动、机场 container_ids 清空等），用模拟服务器记录完整请求序列（方法、路径、请求体原文、顺序）和请求次数，固化为 JSON。重构后逐字节比较。
4. **存储金样（I08）。** 固定一组操作序列（编辑、撤销、批量、导入、刷新合并、提交、未知恢复、维护），在每一步之后导出各表的规范化内容并固化；再加一条：用重构前的程序集打开重构后写过的数据库。
5. **CSV 金样（I06）。** 各资源导出字节与导入后草稿。
6. **清单测试（I09、I10、I12）。** 枚举成员名与数值、全部资源键与占位符、代码和脚本中出现的全部 AutomationId，固化后任何变化都让测试失败。
7. **性能基线（I14）。** 在同一台机器上运行 `--benchmark-core`、`--benchmark-core-load`、`test-data-performance-ui -Rows 50000`、`test-baseline-ui -Rows 50000`，结果归档。
8. **真实数据只读回归。** 复制本机真实数据库到隔离目录，记录加载后的计数、审阅清单内容键和 Pending 任务；之后每个阶段重复比对。不联网，不写原库。

**出口：** 新增金样和清单测试在当前代码上连续两次稳定通过；CI 绿色。

**完成情况（2026-10-20，分支 refactor/r0）：** git 使用 GitHub Desktop 自带副本，每条命令临时信任仓库，不改全局配置；新测试用 xUnit v3 4.0.1。新增 13 项清单测试、38 项请求金样、5 项存储与 CSV 金样和 1 项真实数据库副本比对（未设环境变量时跳过）；与原有 271 项在同一提交上连续两轮全部通过，每组都做过回植错误检查，`src` 无改动。性能基线在文档记录的同一台机器上测得，与“当前表现”基本一致，只有 5 万项变更的提交前检查较慢（总计 1,585 ms，界面最长停顿 403 ms；文档为 1,193／319 ms），结果在本机 `artifacts/r0-baseline`。真实数据库副本比对时原库哈希与修改时间不变，结果在 `artifacts/r0-real-db`，不进 git。CI 配置只在本地，尚未实际运行。

- `step5-v3-baseline` 只能读 v3，不能用来验证“旧程序能打开新库”；需要以后用一个 v4 正式包作为基线。
- 撤销记录中行字段的 JSON 键顺序在不同进程间不固定（存为 ImmutableDictionary）；存储金样按同一条记录保存的字段顺序重排后比较。R3 重写存储时改为固定顺序。

### R1 规范与机械格式化

1. 加入 `.editorconfig`（行宽、花括号、命名、`using` 顺序）。
2. 每个项目单独一次 `dotnet format whitespace` 提交，把多语句同行拆开、长行换行。这是纯空白变化：用确定性构建比较各程序集的 IL（或至少比较反编译输出），证明编译结果不变。
3. 启用分析器（CA1307／CA1309／CA1310 字符串比较、CA2000 释放、可空性警告）。现有告警记入基线文件暂时抑制，新代码清零；字符串比较的修正如果改变行为，归入 A2 单独处理。

**出口：** IL 不变，全部回归通过。

**完成情况（2026-10-20，提交 f5e9291–25fe50b）：** 新增 `.editorconfig`，三个产品项目和三个新测试项目分别格式化、分别提交；旧测试、窗口脚本、resx、xaml 和金样预期文件未动。用确定性构建（不生成调试符号、不在版本号中写入提交哈希）比较 9 个程序集的 SHA-256，格式化和启用分析器后与改动前逐字节相同，并已在最终提交上独立重建复核。一行多条语句已拆开，但单行块（`{ a(); b(); }`）保留在一行：关闭该选项会把所有自动属性拆成三行。格式化工具不会折行，超过 160 字符的行：Core 85→58、Infrastructure 156→134、App 128→119，剩余的在后续阶段改写时处理。分析器（CA1307／CA1309／CA1310／CA2000）现有 207 条告警记入 `docs/analyzer-baseline.txt`（其中旧测试项目 163 条，随旧测试删除而减少），`scripts/check-analyzer-baseline.ps1` 在数量超过基线时失败，已加入 CI。

### R2 领域层

1. 建立 `VamSys.Domain`（由 Core 改名或新建后迁移，见 R-D2），把纯逻辑留在这里，任何 I/O 移出。
2. 建立 `FieldSpec` 表，`Schemas`、CSV 映射、API 字段映射、校验、证据范围、等价比较改为读表。请求金样、CSV 金样和校验消息逐项不变。
3. 建立 `LocalReference`，所有调用点改用它，**先保持各调用点现在的大小写行为**；统一为忽略大小写是 A2 修复，作为单独提交。
4. 明确“只替换行、不原地修改”（A5）：单元格编辑、CSV 合并、删除标记、演示回填改为替换行，并在调试构建中加断言。这是 R3 变更日志的前提。
5. 校验用的静态集合、每行键只算一次（B9）；删除无用代码和重复条件（A7）。

**出口：** 三类金样不变；核心基准不慢于基线。

**实现进度（2026-10-22，分支 `codex/r2-domain-boundary`）：** 字段规格表、`LocalReference`、行替换约束与静态校验集合已合入 `d2abdbb`；本轮补齐程序集和 I/O 边界。

- `VamSys.Domain` 承接模型、字段规格、CSV 内存转换、规划、合并、删除判断、行历史和本地化消息，不引用其他产品项目。
- `VamSys.Application` 仅引用 Domain，承接资源读写与检查点端口、`BatchExecutor`、`DraftEdits`、`WindowCloseController`。这里只建立 R2 所需的边界，R4 的会话、状态门控和用例服务仍待完成。
- `AppPaths` 移到 Infrastructure；缺失资源由 Domain 事件报告，Infrastructure 负责 Trace，App 启动时注册一次。保留 `VamSys.Core` CLR 命名空间与资源基名，CSV、请求、枚举及 v4 保存格式不改。
- Debug 行守卫继续检查原地修改；Release 的 setter 通过 `Conditional("DEBUG")` 省略空守卫调用。架构测试检查实际程序集归属、依赖与宿主 API 调用，金样预期文件保持原样。

**验收状态（2026-10-22）：实现完成，性能验收未通过，R2 未关闭。** Release 395 项、Debug 74 项、金样与清单、两个历史故障回植、正式／QA 包检查均通过；5 万行数据页功能、撤销历史、自动保存与强制退出、关闭失败重试、真实鼠标键盘和 16 种布局组合通过，旧 v4 程序读取新库通过。

默认核心基准各 3 轮、45 项中有 5 项中位数及 14 项 P95 超过 10%；延长预热的独立诊断中，5 万行结构撤销／重做仍约慢 15%。数据页各 3 轮的搜索、翻页 P95、撤销中位数分别约慢 14%、15%、20%；综合窗口测量中的切换与审阅渲染也超线。当前没有足够证据把差异归因为单一代码问题，不能以运行波动豁免门槛。

**后续（2026-10-22）：** 审阅渲染停顿已定位并解决。二分确认来自字段规格表合并中的 efcd91b；渲染代码没有变化，停顿是后台生成 5 万项审阅计划分配约 230–275 MB、触发的后台 gen2 回收落在渲染窗口内。关闭并发回收只把停顿中位数降到约 210 ms 并出现 703 ms 的阻塞，已撤回。审阅计划改为只引用行、提交时再生成完整条目后（合并 8f78b5e），5 万项分配在线 186→77 MiB、演示 223→57 MiB；安静机器上交替各 3 轮，最长停顿中位数 57–63 ms → 12–13 ms，渲染 733–786 → 350–373 ms，计划 753–989 → 345–347 ms。领域层测试按需求重建（合并后单元测试 447 项），删除被取代的 30 个旧场景，覆盖矩阵见 [TEST-COVERAGE.md](TEST-COVERAGE.md)。数据页搜索、翻页、撤销和工作区切换的超线项尚未在安静机器上复测，放到 R3 结束时与存储改造一起测量。

剩余工作限于定位上述超线项、修复后复跑完整性能集合，并补阶段完成标签；此前尚未执行云端 CI 的缺口仍保留。R3 尚未开始，R4 的新项目外壳不计作 R4 完成。当前分支为 `codex/r2-domain-boundary`，实现提交为 `c528359`，QA 检查修正为 `cdf7591`；本轮没有替换既有正式交付包，也没有打 R2 完成标签。全部前后数字、复现方法和证据路径见 [测试报告](TEST-REPORT.md) 与 [性能报告](PERFORMANCE.md)。

### R3 存储层重构

目标是把 `WorkspaceStore` 的 6 个分部文件（约 580 行，SQL、序列化、版本检查和故障注入点交织在一起）重建为职责清晰的存储层，同时**数据库文件格式一字不改**。

1. **结构。** 新项目 `VamSys.Storage`，按职责拆分：
   - `SqliteConnectionFactory`：连接、PRAGMA、WAL、单实例目录锁（`DataDirectoryLease`）。
   - `SchemaV4`：全部表、索引、触发器的 SQL 集中定义在一处，并有测试与现有库的 `sqlite_master` 逐条对照。
   - `Migrations`：旧整块、v2、v3 → v4 各一个迁移类，共用“先备份、单事务、验证回放、最后写 `user_version`”的框架。
   - `WorkspaceRepository`、`JobRepository`、`ResourceRowRepository`、`HistoryRepository`、`SettingsRepository`、`SecretStore`（DPAPI）、`MaintenanceService`（备份与清理）。
   - `RowSerializer`：行、任务、历史步骤的 JSON 读写，改用 System.Text.Json 源生成并复用字段名字符串；输出与现有 JSON 逐字节一致（存储金样证明）。
2. **工作单元。** 一次保存＝一个 `UnitOfWork`：版本号检查、写入、提交在同一事务；提交成功后才通知调用方更新内存。现有“保存失败不改内存”“陈旧版本拒绝覆盖”“检查点与重基线同事务”全部由这一个机制保证，而不是各保存方法各写一遍。
3. **变更日志。** 在 `ResourceData` 和工作区上实现 `ChangeJournal`，所有修改入口都经过它（R2 第 4 步已经堵住原地修改）。存储据此增量写入；日志为空不写；增量保存失败回退到完整保存（与现有 `SaveEdits` 一致）。
4. **定向读写。** 只写头部、只写任务、定向查询（`JobExists`、`LoadJob`、计数）。
5. **后台执行。** 完整保存和读取移到后台：界面线程只捕获不可变数据，读取时显示进度。
6. **故障注入。** 现有可消除的 partial 方法故障点集中为一个 `IStorageFaults` 接口，正式构建注入空实现、编译期移除；测试注入可控实现。正式包检查继续证明不含测试入口。

**验收：** 随机操作序列下，增量保存与完整保存的结果逐表一致；迁移测试、进程终止测试、存储金样全部通过；旧版程序能打开新库。性能目标沿用审查报告：5 万行批量编辑 200 行或新增一行的保存从约 2 s 降到 100 ms 以内（以实测为准），切换工作区和关闭窗口只写日志。
**顺带完成：** B1–B5、原计划第 1、3、4 项。

**进度（2026-10-22）：** 第 1、2、6 项完成（合并“R3：存储层结构拆分”）。新项目 `VamSys.Storage` 含连接、`SchemaV4`、三个迁移类与共用框架、六个仓储、`RowSerializer`、`UnitOfWork` 和 `IStorageFaults`；`WorkspaceStore` 只剩外观（116 行）。存储金样逐字节不变，新增 `SchemaV4` 与新建库 `sqlite_master` 一致性测试，正式包检查通过。`RowSerializer` 仍用反射，未做源生成。第 3、4 项（变更日志、定向读写）见下一段，第 5 项（后台执行）尚未开始。

**进度（2026-10-22，第 3、4 项）：** 完成，第 5 项（完整保存和读取移到后台）仍未开始。`ChangeJournal`（`VamSys.Domain`）保存“数据库与内存上一次一致时”的行对象、字段定义、冲突、快照时间、头部内容键和任务数组，保存时比较它与当前状态得出待写变化，而不是在每个修改入口登记，所以直接操作列表的代码同样被覆盖，不会漏记；只有被执行器原地修改的任务需要 `TouchJob`。`WorkspaceStore` 新增 `SaveChanges`／`SaveHeader`／`SaveJobs`（日志为空不产生任何写入）、`JobExists`／`LoadJob`／`CountRows`，保存连接设置不再序列化再反序列化整个工作区；界面的 `Save()`（含关闭和切换工作区）改走日志，冲突处理改为对话结束时保存一次，连接与提交任务只写头部／任务，演示执行器每条只写任务，提交确认、诊断导出、维护预览改用定向读取。5 万行工作区上批量编辑 200 行、新增一行从约 2 s 降到 7–40 ms，写入行数从 20 万次降到 200 和 1，解决 100 个冲突从 100 次（2.4 GB JSON）降到 1 次，无改动的切换工作区不再写入；表见 [性能报告](PERFORMANCE.md)。随机序列（8 个种子×700 步）下日志保存的库与完整保存的库逐表逐列一致，注入失败、陈旧版本、日志不可信的各条回退路径均有测试；存储、请求、CSV 金样未重新生成。仍整体重写的情形：从未保存的工作区、保存失败后的下一次、大范围重排或靠前位置的删除、任务检查点触及的资源、被标记的任务。详见 [Storage 说明](../src/VamSys.Storage/README.md)。

### R4 应用层

1. 建立 `WorkspaceSession`，`MainWindow` 不再直接调用存储。
2. 建立 `InteractionGate`，替换分散的标志；用无窗口测试覆盖所有状态转换（特别是关闭、对话框、执行中、存储受阻的组合）。
3. 按用例逐个抽出服务，**每次一个服务、一个提交**，顺序从依赖少的开始：诊断 → 维护 → 导入导出 → 批量编辑与机型分配 → 连接与刷新 → 审阅 → 提交 → 任务恢复（未知项各出路、放弃、删除核对）。每个服务都有无窗口测试，旧的 `MainWindow` 代码改为调用它。
4. 抽出 `DemoRunner`，把演示回填从界面代码移出（行为不变）。

**出口：** `MainWindow` 不含业务判断；全部回归与 `test-v3-verification-ui`、`test-recovery-ui`、`test-settings-save-ui` 通过。

**进度（2026-10-25，refactor/r0 2270a96）：** `DataRow` 改为不可变（只能创建时赋值，修改用 `With`／`Builder` 生成新行），正式版也由编译器保证，去掉了 Debug 守卫；5 万行规划分配 132→52 MB。`InteractionGate` 取代 7 个分散的状态标志，`WorkspaceSession` 接管保存锁、待保存编辑、自动保存和切换工作区；拆出维护、诊断、导入导出、批量编辑、连接、冲突、审阅、提交、任务恢复服务和 `DemoRunner`；`MainWindow*.cs` 2666→2276 行，不再引用存储和这些标志。新增约 150 项单元测试。剩余：页面里的 `Work`／本地操作控件开关随 R6 处理；`ReviewDisposition` 的枚举清单仍用旧位置映射，待旧测试清理时改为如实记录；本阶段的窗口脚本验收尚未运行。详见 [简报结果](agent-briefs/R4-application-services.md)。

### R5 API 层重构

新项目 `VamSys.Api`，从最底层的 HTTP 往上逐层重建。每一层单独可测，上层只依赖下层接口。

1. **传输管线**（最底层）。把 `OperationsTransport` 拆为：
   - `RequestBudget`：进程全局 60 次／分钟、间隔 1 秒、Retry-After、剩余额度为零等待 60 秒（现有 `RequestCoordinator` 的逻辑原样迁移，配 `TestTime` 精确测试）。
   - `TokenProvider`：OAuth 取令牌与按 `expires_in` 缓存。
   - `ResponseClassifier`：把 HTTP 结果分为“未发出／被拒未处理（429、401、403）／已接受／结果未知（超时、5xx）／字段错误（422）”，供执行器决定状态。现在这个分类散在多处，是写入安全的核心，单独成类并穷举测试。
   - `ReadRetryPolicy`：只用于 GET 的退避重试；写入路径在类型上就拿不到重试策略（I01 由编译保证）。
   - `Pager`：游标分页、页长退回默认、同源与路径限制、循环页检测。
   - `ApiDiagnostics`：脱敏、1 MiB 截断、哈希。
   - 底层改用 `SocketsHttpHandler` 并设置连接寿命（B10）。
2. **映射与规划。** 从 `OperationsAdapter` 拆出每类资源的 `ResourceMapper`（JSON → 行）和 `RequestPlanner`（变更 → `ApiStep` 序列），都读取 R2 的字段规格表；契约校验（请求属性、类型、必填、枚举）独立为 `ContractValidator`。
3. **核对。** `ReadbackVerifier`、`DuplicateFinder`（候选查询与 `CandidateIndex`）、`IdentityIndex`（机场别名等）、`DeletionVerifier`、`UnknownResolver`、`MovementVerifier` 各自独立，`OperationsMovement`、删除核对、未知恢复改为组合它们。
4. **执行流水线。** 执行器主循环拆成预检 → 判重 → 写入 → 回读 → 检查点各段；条目改用 Id／LocalId 字典，快照按位置删除（B7）。检查点写入通过 R3 的工作单元。
5. **缓存。** 按行缓存从 `RawApiJson` 解析出的少量字段（B8）。

**验收：** 请求金样逐字节一致、请求次数一致；进程终止测试通过；`test-api-ui-20k` 通过。演示是否改为共用在线流水线属于行为变化，见 R-D6。

**进度（2026-10-25）：** 第 1–5 项完成，仍在 `VamSys.Infrastructure` 内（`Transport/`、`Api/`、`Api/Pipeline/`），没有另建 `VamSys.Api` 项目。`OperationsAdapter` 677→115 行、`OperationsBatchExecutor` 345→17 行，只剩外观；写入路径在类型上拿不到重试策略，并有反射测试兜底。请求金样逐字节不变、请求次数不变；新增单元测试约 420 项、集成测试约 70 项。`SocketsHttpHandler`（B10）未改。`test-api-ui-20k` 和旧测试中的传输、适配器场景清理尚未做。详见 [简报结果](agent-briefs/R5-adapter-executor.md)。

### R6 界面重构

界面层全部重建，包括数据表。功能、文案、AutomationId、键盘行为保持不变；视觉布局在“同等或更清楚”的范围内统一整理，不增删功能入口。

1. **Presentation 项目。** 建立 `VamSys.Presentation`，每个页面一个 ViewModel（沿用 CommunityToolkit.Mvvm），只依赖 Application；命令的可用状态全部来自 `InteractionGate`，不再在页面里写条件。对话框（确认清单、冲突审阅、未知核对、机型选择、批量预览、导入映射）各有自己的 ViewModel，经 `IDialogService` 打开。
2. **外壳。** 标题栏、工作区选择、导航、状态栏、页面宿主重写为 XAML；导航经 `InteractionGate` 判断，提交交接的“解锁前切到任务页”由导航服务提供专门方法，不能绕开。
3. **样式与主题。** 先建立 `ResourceDictionary`：颜色令牌、间距、字号、舒适／紧凑行高、单元格和按钮样式，明暗与高对比度各一套；后续页面只引用资源。
4. **页面。** 逐个改为 XAML＋`x:Bind`，**每次一个页面**，顺序：设置 → 工作区 → 导入 → 任务 → 审阅。每个页面迁移后：AutomationId 清单不变、本地化切换不重建页面、选择与滚动保留。
5. **数据表组件**（最后做，单独一个子阶段）。
   - 新建 `DataGrid` 控件及其 ViewModel 接口：列、行模型、当前页、选择、编辑状态、锁定状态。
   - 先在 QA 构建中让新旧数据表可切换（隐藏开关），用同一批 2 万／5 万行数据并排测量；新控件达到性能门槛前，正式包继续用旧控件。
   - 必须保留的设计：只为当前页建行模型；行容器回收时保留单元格控件只换数据；单元格平时是文本、编辑时才建输入框；锁定时屏蔽输入而不切 `IsEnabled`；编译绑定；回调经弱引用的行链接读取当前工作区。
   - 键盘与自动化：Enter／Esc／Tab、跨页选择、读屏报告为可编辑控件，与旧控件逐项对照（`test-cell-editing-ui`）。
   - 达标后删除旧控件和隐藏开关。
6. **任务页列表**同样按“只建可见内容、只更新变化的记录、50 ms 合并呈现”重写，与旧实现用 `--seed-task-ui`（1,000 条历史＋2 万条活动项）对照。
7. **QA 扩展。** 窗口控制和故障注入改为通过接口注入，只在 QA 构建中提供实现；正式包检查继续证明不含测试入口。

**验收：** 全部窗口测试通过；5 万行数据页（打开、搜索、翻页、编辑、撤销、滚动）、启动和任务页不慢于基线 10%，原生内存增长不高于基线；明暗主题、高对比度和 100％／150％／200％ 缩放人工查看；读屏器抽查主要页面。

**进度（2026-10-27，refactor/r0 2d5dd17）：** 第 1 步的第一部分完成：新建 `VamSys.Presentation`（只依赖 Domain、Application，有反射测试兜底），搬入单元格、行、数据表、任务页 ViewModel；App 和测试改为引用项目，不再链接源文件；WinUI 专用的单元格边距改由 App 中的 `CellLayout` 提供。顺带修复：`scripts/build.ps1` 的行为关卡改为运行快速检查（旧运行器已无场景）；`docs/STORAGE-MIGRATION.md` 自 R3 起链接源码，导致正式包检查失败，已改为纯文字。正式包与 QA 包均通过打包检查；窗口验收：`test-cell-editing-ui` 与 `test-ui-interactions -Rows 20000` 通过（舒适 6 行、紧凑 9 行，与基线一致）。后续按以下顺序推进：样式资源 → 外壳（导航经 `InteractionGate`，提交交接专门方法）→ 页面（设置 → 工作区 → 导入 → 任务 → 审阅，对话框接入 `IDialogService`）→ 数据表组件（QA 构建中新旧并排对比）→ 任务页列表与 QA 扩展注入。

**进度（2026-10-27，refactor/r0 360f10f）：** 第 3 步样式资源完成：新建 `Theme.xaml`，含字号、间距、边距、圆角、两档行高与单元格尺寸、控件宽度、对话框高度，以及明、暗、高对比度三套画刷别名（均指向原系统画刷）。XAML、运行时加载的任务卡片与审阅分组模板，以及代码拼装的页面，约 70 处写死的值改为引用资源，数值不变；代码经 `UiTokens` 按类型读取。新增 `ThemeTokenTests`，核对每个被读取或引用的键都有定义、三套主题别名一致，回植验证有效；打包检查要求 `Theme.xbf`。窗口验收：`test-ui-interactions -Rows 20000` 通过（舒适 6 行、紧凑 9 行，16 种布局，审阅 22 项）；`test-v3-verification-ui -Tasks` 通过（1,001 个任务，本地更新 P95 1.1 ms，创建 7 张卡片、30 个详情）。

**进度（2026-10-27，refactor/r0 0e72b2a）：** 第 2 步外壳完成：`VamSys.Presentation` 新增 `ShellViewModel`，持有当前页（`ShellPage` 枚举），给出页面标题、模式徽标与资源栏显隐。所有导航经 `InteractionGate`，提交交接改用 `ShowTasksBeforeUnlock`，持锁时仍切到任务页。窗口按枚举选导航项，6 处 `MenuItems[n]` 下标跳页已移除。新增 18 项外壳测试（共 21 个用例），含 XAML 导航项顺序核对；提交交接测试经回植验证有效。资源键清单中 9 个页面标题与徽标键由直接引用改为间接引用，键不变。窗口验收：`test-ui-interactions -Rows 20000` 通过。`test-localization-ui` 在“中英文就地切换”通过后，于点击“Create demo workspace”前调用工具栏溢出按钮时报 UIA 错误；用外壳之前的提交（cb1ff77）构建的正式包同样失败，属已有问题，不是本步引入。随后已查明并修复（D3），见下条。现有窗口脚本均未覆盖“演示提交后切到任务页”。

**进度（2026-10-27，D3）：** `test-localization-ui` 已修复，三段检查全部通过。原因是脚本的点击辅助函数：按钮一时找不到就立即打开命令栏溢出菜单，而刚选中的页面还没布局，UIA 调用失败。旧报告记的“Apply to drafts”失败也是这个原因。另补两处脚本落后于 10-19 界面改动的地方：恢复删除要在预览中确认；审阅页按资源导出 CSV，演示工作区只有模拟提交。脚本改为带 BOM 的 UTF-8（PowerShell 5.1 下尚未实测，三段检查在 PowerShell 7 下通过）。产品代码未改。

**进度（2026-10-27，refactor/r0 59d4bfc）：** 第 4 步第一个页面（设置）完成：`SettingsPage.xaml`（编译绑定，无 `x:Name`）加 `SettingsViewModel`。该模型管理输入、页面文字、语言选择和两种保存，客户端密钥只经过、不保存；按工作区缓存，语言切换由窗口刷新。新增 9 项无窗口测试。清单变化：两个状态键改为间接引用，`LanguagePicker` 改由 XAML 定义，并同步了 D3 修复后脚本查找的控件名。窗口验收：`test-localization-ui`、`test-credentials-ui`、`test-settings-save-ui` 全部通过。

**进度（2026-10-27，refactor/r0 9348bb6）：** 第 4 步第二个页面（工作区）完成：`WorkspacePage.xaml` 加 `WorkspaceViewModel`／`ResourceCardViewModel`。每类资源一张卡片，改动数在显示时取窗口缓存；页面仍随每次渲染重建，语言切换时刷新当前页，切换工作区时释放引用。新建工作区与创建演示工作区的操作抽成窗口方法，对话框待接入 `IDialogService`。新增 3 项无窗口测试，经回植验证。清单无变化。窗口验收：`test-localization-ui`（含点击创建演示工作区）与 `test-ui-interactions -Rows 20000` 通过。

**进度（2026-10-27，refactor/r0 46650d9）：** 第 4 步第三个页面（导入）完成：`ImportPage.xaml` 加 `ImportViewModel`，语言切换刷新当前页；新增 1 项无窗口测试，经回植验证。现有窗口脚本均不打开导入页，这一页没有窗口验收，需在 R7 的 C# 窗口测试或人工检查中补上。

**进度（2026-10-28，refactor/r0 6c67f27）：** 第 4 步第四个页面（任务）完成，同时覆盖第 6 步的任务列表部分。新增 `TaskListViewModel`：任务按新的在前排列；进度变化立即计入模型，界面每 50 ms 合并呈现一次（定时器经 `IAutosaveTimer` 注入），立即变化当场刷新。`TasksPage.xaml` 用编译绑定替换 `XamlReader` 反射模板，列表仍虚拟化。新增 7 项无窗口测试，经回植验证。`TaskJobsList` 改由 XAML 定义。另修复构建偶发文件锁（22f359d）：Infrastructure 向 Storage 传了空的 `VamSysTestSupport`，Storage 因此以两个实例并行写入同一 obj 目录。窗口验收：`test-v3-verification-ui -Tasks` 通过（1,001 个任务，本地更新 P95 1.3 ms，最大 3.8 ms，页面构建 1 次）；`test-recovery-ui` 通过。创建的卡片数（脚本在第一次看到卡片时计数）本来就随时机波动：新版连跑 4 次为 11、8、6、5 张，旧版记录为 7、8 张，均远低于 100 张门槛，不是本步引起的变化。

**进度（2026-10-28，refactor/r0 79b8401）：** 第 4 步第五个页面（审阅）完成：`ReviewPage.xaml` 加 `ReviewViewModel`／`ReviewRowViewModel`。页面先显示准备中，计划在后台建好后填入；模板选择器与编译绑定替换 `XamlReader` 反射模板；改动文字移到 `ReviewTexts`，与提交确认共用。新增 12 项无窗口测试。窗口验收中发现并修复一个本阶段引入的缺陷（79b8401）：审阅分组标题和工作区页资源卡片显示成“[航线]”。原因是资源名是 `MessageDescriptor`，误用 `Get` 时被隐式转成中文原文当键查找。`ILocalizationService` 增加了禁止使用的 `Get(MessageDescriptor)` 重载，此类调用从此无法编译；全解决方案没有别处误用。窗口验收：`test-localization-ui`、`test-ui-interactions -Rows 20000` 通过。`test-baseline-ui -Rows 50000` 单轮结果（各项为 3 次重复）：5 万项审阅计划 506／624／675 ms，渲染 597／862／648 ms，渲染最长停顿 84／334／43 ms；10-22 修复后同机交替测量为计划约 346 ms、渲染约 360 ms、停顿 12–13 ms。该单轮测量排在数据页、滚动和切换之后，又紧接其他窗口脚本运行。随后与 R6 之前（05b74dc）同机交替各测 3 轮：当前版本渲染 472／379／372 ms、停顿 18.3／14.8／14.6 ms，R6 之前为 679／521／374 ms、22.1／16.4／15.3 ms，没有回退，详见 [性能报告](PERFORMANCE.md)。

**进度（2026-10-28，数据表 D1，refactor/r0 cbffe8a）：** 方案见 [R6-DATAGRID.md](R6-DATAGRID.md)。D1 已移入 Presentation 的三块，窗口改为调用，旧控件不变：单元格只读、行状态与机队标签（`DataGridRules`）；选择计数（`SelectionCounts`）与数据页控件可用状态（`DataGridAvailability`）；单元格编辑提交（`CellEditCommitter`，替换行、同格连续输入合并撤销步骤、改动计数增量）。共新增 31 项无窗口测试，每块均经回植验证。窗口验收：`test-cell-editing-ui`、`test-ui-interactions -Rows 20000` 通过；`test-data-performance-ui -Rows 20000` 通过（首次展示 478 ms，搜索 124 ms，翻页 P95 11.2 ms，语言 P95 25.7 ms，单格处理 P95 1.3 ms，当前页 200 行／4,000 个单元格模型），均在门槛内。D1 第四块：行池、`RowLink` 与行、单元格模型的创建移入 `DataGridViewModel`，窗口经 `IDataGridHost` 提供当前状态和编辑后的通知；另加 8 项无窗口测试，9 处回植均被抓到。顺带修正一处潜在问题：列或密度变化重建行池时，若数据未变，新 `RowLink` 不会绑定，单元格会拒绝编辑。第四块窗口验收（f6f9d1f 的 QA 包）：`test-cell-editing-ui`、`test-ui-interactions -Rows 20000` 通过；`test-data-performance-ui -Rows 50000` 通过（首次展示 367 ms，搜索 233 ms，翻页 P95 8.4 ms，语言 P95 21.7 ms，单格处理 P95 0.6 ms，当前页 200 行／4,000 个单元格模型，脚本结束时工作集 692 MiB），单次测量，不作提速或回退结论。切换工作区后的内存由 `test-baseline-ui` 测量（5 轮往返后强制回收再读）：5 万行切换后工作集 671 MiB、托管堆 255 MiB，同日 D1 前（08:23）为 730／255 MiB；2 万行 580 MiB、托管堆 112 MiB。托管堆与 D1 前相同，说明切换后旧工作区没有被单元格留住；同样是单次测量。D1 完成。

**进度（2026-10-28，数据表 D2 第一块）：** 新建 `DataGrid.xaml` 控件与 `DataGridFooterViewModel`，仅 QA 构建可用 `VAMSYS_QA_DATAGRID=new` 切换；正式包仍用旧表格，打包检查已证明正式包不含开关。页脚模型 4 项无窗口测试，6 处回植均被抓到；AutomationId 清单只多出同名 id 的新来源，没有新 id。窗口验收（f3bd13a 的 QA 包，`VAMSYS_QA_DATAGRID=new`）：`test-cell-editing-ui`、`test-ui-interactions -Rows 20000` 通过；`test-data-performance-ui -Rows 50000` 通过（首次展示 347 ms，搜索 228 ms，翻页 P95 10.1 ms，语言 P95 23.1 ms，单格处理 P95 0.7 ms，200 行／4,000 个单元格模型），其结果记录 `dataGrid=new`，证明开关生效；单次测量，新旧对比留给 D3。缺口：现有窗口脚本都不经 UI 自动化点击页脚（翻页按钮、跳页框、每页条数、选择菜单、取消查询），翻页与条数走 QA 命令直接调用窗口方法，所以新旧页脚的点击路径都没有窗口证据；D3 前需补一个按 AutomationId 点击页脚的脚本。已补：`test-data-footer-ui.ps1 -DataGrid old|new`，新旧控件均通过，两边找到的页脚 AutomationId 与英文读屏名称一致。它在旧页脚上发现跳页框和每页条数的读屏名称换语言后不更新（创建时设一次），正式包仍用旧页脚，已在语言刷新时补上。取消查询按钮只验证了空闲时不出现，查询进行中的点击仍无窗口证据。

**进度（2026-10-29，数据表 D2 第二块）：** 列标题、行高样式、锁表、行源同步与选择应用从窗口移入共用的 `DataTableView`（见 [R6-DATAGRID.md](R6-DATAGRID.md)），新旧两条路径都改走它，所以正式包的旧表格也变了。Quick 检查、两个包通过；AutomationId 清单只是 `DataTable` 的来源文件变了。窗口验收（b5dad3b 的 QA 包）：新旧控件各跑 `test-cell-editing-ui`、`test-ui-interactions -Rows 20000`、`test-data-performance-ui -Rows 50000`、`test-data-footer-ui`，8 次全部通过。5 万行旧／新：首次展示 338／341 ms，搜索 246／226 ms，翻页 P95 9.1／9.1 ms，语言 P95 21.5／23.6 ms，单格处理 P95 0.8／0.7 ms，结束时工作集 694／694 MiB；各一次，不作快慢结论，留给 D3。

**进度（2026-10-29，数据表 D2 第三块）：** 选择写回、选择本页、换密度锚点偏移移入 Presentation（`UserSelection`、`DataGridScroll`），滚动偏移读取与首个可见行移入 `DataTableView`；5 项无窗口测试，7 处回植均被抓到；Quick 检查与两个包通过。新旧两条路径都受影响。窗口验收（0c45024 的 QA 包）：旧表格 4 个脚本全部通过（5 万行首次展示 480 ms，搜索 230 ms，翻页 P95 10.3 ms，语言 P95 24.3 ms，单格 P95 0.8 ms）；新控件 `test-ui-interactions -Rows 20000`、`test-data-performance-ui -Rows 50000` 通过（首次展示 340 ms，搜索 219 ms，翻页 P95 11.6 ms，语言 P95 25.5 ms，单格 P95 0.8 ms）。新控件的 `test-cell-editing-ui` 与 `test-data-footer-ui` 起初两次都停在"QA 窗口无法置前"的前置检查（桌面被占用，没有执行任何输入），桌面空闲后重跑均通过。至此新旧控件 4 个脚本全部通过。

**进度（2026-10-29，数据表 D3）：** 新增 `scripts/measure-datagrid-ab.ps1`，新旧控件同机 3 轮交替测量 5 万／2 万行数据页与 5 万行滚动、内存，18 次全部通过。5 万行各项中位数都在门槛内（打开 454→344 ms，搜索 224→222 ms，翻页 7.83→6.75 ms，单格编辑 0.57→0.62 ms，撤销 5.37→4.97 ms，滚动每帧 16.75→16.81 ms），原生内存增长 274→271 MiB，不高于旧表格。详见 [R6-DATAGRID.md](R6-DATAGRID.md)。进入 D4 前还差主题、高对比度与缩放的人工查看。

**进度（2026-10-29，数据表 D4）：** 按用户决定，主题、高对比度与缩放的人工查看推迟到 R8 验收，先做替换。正式包改用 `DataGrid`，删除旧表格、旧页脚与 QA 开关（窗口代码净减约 160 行）。AutomationId 集合不变（清单只删去旧代码来源的行）；Full 检查通过（单元 1605、集成 484、分析器 47／47），两个包通过。窗口验收（9cfbeeb 的 QA 包）：`test-cell-editing-ui`、`test-ui-interactions -Rows 20000`、`test-data-performance-ui -Rows 50000`（首次展示 333 ms，搜索 227 ms，翻页 P95 7.5 ms，语言 P95 22.2 ms，单格 P95 0.7 ms）、`test-data-footer-ui`、`test-baseline-ui -Rows 50000` 全部通过。5 轮切换后托管堆 255 MiB，与 D1 前、D1 后相同，没有留住旧工作区；工作集 737 MiB，比 D1 同脚本单次的 671 MiB 高，但 D3 交替测量中旧表格本身在同类条件下为 773 MiB，单次结果不能归因于新控件。R6 数据表（D1–D4）完成；主题、高对比度与缩放的人工查看在 R8。

**进度（2026-10-29，R6 对话框第一块）：** Presentation 新增 `IDialogService`、`DialogViewModel` 与两个通用模型 `MessageDialogViewModel`（文字，可滚动、可限高）、`ListDialogViewModel`（可选多行表头、条目、限高、是否可选中）；App 新增 `MessageDialogView.xaml`、`ListDialogView.xaml`（编译绑定，无 x:Name）和窗口内的 `DialogHost`。限高以 `DialogHeight` 命名，在 App 映射到原来的主题令牌；取消始终提供且为默认按钮，`gate.BeginDialog` 与 QA 的对话框观察不变。迁移了 23 处文字与列表对话框：校验结果、导出问题与删除确认、导入基线警告、备份清理、放弃未发送项、未知核对的 7 个确认、批量删除问题与预览、恢复／规则编辑／退役三个批量预览（`BatchPreview` 改为返回模型）、机队问题与预览、审阅问题、提交清单与删除确认。仍走旧路径、下一块再做的：航司参数选择、机队选择、任务详情、导入映射、冲突审阅、未知核对的远端 ID 输入、批量规则表单、新建工作区名称。新增 4 项无窗口测试；Quick 检查与两个包通过，清单无变化。窗口验收（e31cfb0 的包）：`test-localization-ui`、`test-recovery-ui`、`test-ui-interactions -Rows 20000` 通过。其中真正打开已迁移对话框的只有批量删除预览与恢复预览（`test-localization-ui` 点击确认）；`test-recovery-ui` 的远端 ID 输入与 `test-ui-interactions` 的规则编辑表单仍是旧路径。其余 21 个已迁移对话框没有窗口脚本打开，R7 补窗口测试时覆盖。

**进度（2026-10-29，R6 对话框第二块）：** 新增 `TextInputDialogViewModel`（段落＋单行输入）、`ChoiceDialogViewModel`（`DialogChoice` 的读屏名为其标签）、`TaskDetailsDialogViewModel`（直接用任务页的条目模型，运行中仍随状态更新），文字对话框加第二段 `Detail`；对应 XAML 视图编译绑定、无 x:Name。迁移冲突审阅（原先自建 `ContentDialog`、不经 QA 观察，现改走统一入口）、新建工作区名称、未知核对的远端 ID 输入、航司参数选择、任务详情（`TaskDetailsList` 改在 XAML 声明）；`Choose` 已无调用而删除。仍走旧路径的只剩机队选择、导入映射、批量规则表单。新增 3 项无窗口测试；Quick 检查与两个包通过；清单只有 `TaskDetailsList` 的来源变化。窗口验收待跑。

**进度（2026-10-29，R6 对话框第三块）：** 最后三个表单对话框改为模型＋XAML：`BatchRuleDialogViewModel`（列、规则种类、查找、新值；退役时列选择不可用）、`ImportMappingDialogViewModel`（每列一个目标框、基线勾选、样例；同名列按最后一个框映射，与原先一致）、`FleetChoiceDialogViewModel`（替换／追加／移除、搜索、每个机队一个勾选框；飞机只能选一个，移除方式允许选择不可用的机队）。窗口的旧入口 `Confirm(标题, UIElement)` 已删除，`DialogHost` 只接受对话框模型，所有对话框都经 `IDialogService` 打开（R6 第 1 步的对话框部分完成）。新增 4 项无窗口测试，8 处回植均被抓到；Full 检查通过（单元 1616、集成 484、分析器 47／47），两个包通过，清单无变化。窗口验收（7cb5e56 的包，覆盖第二、三块）：`test-localization-ui`（机队选择：按标题与机队名找到勾选框、预览并应用）、`test-recovery-ui`（远端 ID 输入反复打开、重启后保留、中英文）、`test-ui-interactions -Rows 20000`（批量规则表单打开时锁定、取消后不改草稿）、`test-v3-verification-ui -Tasks`（任务详情列表只建可见行）全部通过。导入映射、冲突审阅、新建工作区与航司参数选择没有窗口脚本打开，留给 R7。

**进度（2026-10-29，R6 外壳启用状态）：** 窗口原先在 5 处各自设置导航、工作区与资源选择、页面宿主、窗格按钮和进度环（独占工作、本地操作、审阅工作锁、关闭保存、数据查询），规则略有出入。现由 `InteractionGate` 新增的 `NavigationEnabled`、`ContextPickersEnabled`、`PageEnabled`、`PaneToggleVisible`、`ShowsActivity` 给出，窗口只在 `ApplyShellState()` 一处应用；删除不再使用的 `ShowsProgress`、`ShowsWorkProgress`。两处有意的变化：关闭保存期间资源选择也禁用（原先只禁用工作区选择，它此时本就不起作用）；取消查询时若本地操作仍在进行，进度环不再提前熄灭。组合测试改为核对新查询，另补"仅本地操作"的断言；7 处回植均被抓到。Quick 检查与两个包通过。窗口验收待跑。

**进度（2026-10-29，R6 第 7 步 QA 扩展）：** 存储的故障注入原本已是接口（`IStorageFaults`，实现只在测试构建）；唯一的缺口是 QA 窗口经反射写存储的私有 `Barrier`，现改为调用只在测试构建中存在的公开 `StorageTestHooks.SetBarrier`。窗口原先的三个分部方法（主题覆盖、启动命令管道与屏障、观察对话框）收为一个 `IWindowVerification` 接口，QA 构建经唯一的 `CreateVerification` 提供实现 `WindowVerification`，正式构建里该字段为空。打包检查新增：`CreateVerification` 与 `WindowVerification` 只在 QA 包、`StorageTestHooks` 只在 QA 包的存储程序集中。两个包通过。窗口命令本身仍是窗口的分部类（需要读取窗口内部状态），改为独立的 C# 窗口测试属于 R7。

**窗口验收（2026-11-02，外壳启用状态与 QA 扩展两块）：** `test-v3-verification-ui` 的 `-Autosave`、`-History`，`test-ui-interactions -Rows 20000`，`test-data-performance-ui -Rows 50000`（首次展示 338 ms，搜索 209 ms，翻页 P95 7.6 ms，语言 P95 21.5 ms，单格 P95 0.6 ms）通过；自动保存与历史两项依赖存储屏障的计数（行保存 2 次、完整保存 0 次），说明去掉反射后屏障照常工作。默认模式（关闭冻结、保存失败恢复、重试关闭）起初失败：QA 屏障只拦 `Full:BeforeCommit`，而 R3 起关闭保存走日志保存（`Journal:BeforeCommit`），屏障拦不住，窗口真的关闭，随后读取导航控件报 0x8000FFFF。在 a3b6611（本两块之前）构建的 QA 包上同样失败，所以这是 R3 起就存在的测试装置问题、与本两块无关；该模式上次通过的记录在 10-19。修正为同时拦截日志与完整保存的提交点后，三种模式全部通过；该检查同时证明关闭保存期间导航被禁用、失败后恢复。

### R7 测试体系重建

现有测试是一个按顺序执行、失败即停的控制台程序（`Program.cs` 约 460 行负责调度、种子和断言入口），场景文件按“审核轮次”而不是按功能组织，很多断言绑在现有内部结构上（具体方法、分部类、内部字段），重构后大多会失效；界面逻辑靠链接源文件编译；窗口测试是 PowerShell 脚本，部分因编码无法在 5.1 运行，`test-localization-ui` 还有已知失败。因此不迁移旧测试，而是以第 3 节的需求为依据、针对新结构重新建立。目标结构：

| 项目 | 内容 | 依赖 |
|---|---|---|
| `tests/VamSys.TestSupport` | 虚拟时钟、模拟 Operations 服务器（可编排响应、记录请求序列）、隔离数据目录、数据构建器（替代散落的 `Fixture()`）、金样比较工具 | 无 UI |
| `tests/VamSys.UnitTests` | Domain、Application、Presentation 的纯单元测试，按被测类组织，每个用例独立、可并行 | 项目引用，不链接源文件 |
| `tests/VamSys.IntegrationTests` | 存储（真实 SQLite）、API（模拟服务器）、执行流水线、迁移、进程终止恢复、金样；不能并行的放入顺序集合 | 子进程入口保留 |
| `tests/VamSys.UiTests` | 窗口测试改用 C#＋UI 自动化库（建议 FlaUI，见 R-D9）驱动 QA 包，替代 PowerShell 脚本 | QA 包 |
| `tests/VamSys.Benchmarks` | `--benchmark-core` 等基准，独立于回归 | 无 |
| `tools/VamSys.QaSeed` | `--seed-*`、`--assert-*`、`--verify-package` 等命令行工具，从测试运行器中分出 | 无 |

做法：

做法：

1. **以需求为准，不以旧测试为准。** 建立需求覆盖矩阵：第 3.1 节每个功能（F01–F26）、第 3.2 节每条规则（I01–I15）列出它的新测试。CI 检查矩阵中每一行至少有一条通过的测试；没有测试的需求不能宣布完成。
2. **按新结构分层设计。**
   - 单元测试针对 R2–R6 的新组件：字段规格表、`LocalReference`、`ChangeJournal`、工作单元、`ResponseClassifier`、`RequestBudget`、`Pager`、`ReadbackVerifier`、`InteractionGate`、各用例服务、各 ViewModel 和对话框模型。
   - 集成测试针对层与层的契约：存储（真实 SQLite、迁移、故障注入）、API（模拟服务器、请求序列）、执行流水线（检查点、未知恢复、进程终止）。
   - 窗口测试只覆盖用户流程和界面专有行为（键盘、滚动、本地化、关闭、密度、主题），业务判断不在窗口里重复测。
3. **写入安全用状态表穷举。** 写入结果判定（未发出／被拒／已接受／未知／字段错误）× 资源 × 操作 × 中断时刻，用参数化测试列满，而不是像旧测试那样逐个历史问题补一条。
4. **每阶段的节奏。** 某一层重构完成 → 为该层按矩阵编写新测试 → 新测试全部通过且金样不变 → 删除该层的旧测试。旧测试在被删除前仍在 CI 中运行，作为额外保护；它若因内部结构变化而失效，可以直接删除，但对应需求必须已有新测试。
5. **保留的素材（不是保留旧测试）。** 这些是从真实问题和真实服务器积累的数据或做法，新测试直接复用：`Fixtures/vcca-weight-20261020.json` 的真实响应回放；进程终止恢复的“模拟服务器进程＋执行进程”做法与中断时刻清单；两种虚拟时钟；旧版本基线 `artifacts/step5-v3-baseline`（用于升级测试）；审查和更新日志中登记过的每个已修复问题，各写成一条新的回归用例。
6. **窗口测试重写。** 11 个 PowerShell 脚本按新界面重新编写为 C# 测试，不逐行翻译；旧脚本在对应页面重构完成、新测试通过后删除。编码问题和 `test-localization-ui` 的已知失败随之消失（D3）。
7. **测试本身的有效性。** 推广 `test-history-mutations.ps1` 的做法：对不重发、回读、检查点顺序、判重、版本号、陈旧覆盖等关键判断各准备回植错误，确认新测试能发现，作为 R8 的验收条件。
8. **运行时间。** 单元测试并行后目标 10 秒内；完整集成测试不超过现在的约 30 秒。

**进度（2026-10-26，refactor/r0 37b5d57）：** 旧测试集中清理完成（简报 C1–C7，见 `docs/agent-briefs/`）。旧运行器场景 233→32；新测试单元 1,472 项、集成 437 项；分析器告警 171→62。清单测试不再把新位置映射回旧位置，如实记录当前路径和程序集。历史变异脚本改为对新测试回植错误，两种错误均被拦下。测试清理只释放本目录的 SQLite 连接池，消除并行测试的偶发失败。旧运行器剩余：真实进程终止恢复（4 组）、v3 旧程序拒绝 v4 库（1 项），以及 `Program.cs` 中 13 个演示执行器和多工作区场景，待下一轮迁移；命令行入口（种子数据、断言、打包检查、基准、诊断回放）保留。集成测试目前约 160–190 秒，主要是标为 Slow 的日志等价测试和真实数据库测试，未达到“约 30 秒”的目标。

**进度（2026-10-26，C8／C9）：** 旧运行器的场景全部迁完（32→0），`tests/VamSys.Tests` 只剩命令行入口，不带参数运行时提示用法并以退出码 2 结束；完整档去掉旧运行器一步。C8：13 个演示执行器与存储场景迁为 `UnitTests.Application.BatchExecutorTests`、`UnverifiedAdapterTests` 和 `IntegrationTests.Storage.WorkspaceIsolationTests`，每条“不重发”都断言写入次数，并新增 F01 的直接隔离测试（同库两个工作区重开后逐字节比对、密钥互不可见）。C9：新建子进程宿主 `tests/VamSys.ProcessChild`，真实进程终止的 18 个边界迁为 `IntegrationTests.Storage.ProcessTermination.ProcessTerminationTests`（Slow，约 17 秒），v3 旧程序拒读 v4 迁为 `V3BinaryRefusalTests`（主检出有基线时实际运行并通过，缺失时跳过）；回植验证四组均能拦下。新测试单元 1,484 项、集成 459 项；分析器告警 62→47。合并时修正：v3 测试改为只清本库连接池；C9 新文件的 8 条告警（快速档未报出）已修。

**R7 剩余工作的顺序（2026-11-02 定）：** ① 覆盖矩阵更新到现有测试并加自动检查；② 命令行工具拆出（`tools/VamSys.QaSeed`：种子、断言、打包检查、诊断回放；`tests/VamSys.Benchmarks`：基准），并为打包检查补测试；③ 写入安全状态表的参数化测试；④ `tests/VamSys.UiTests`（C#＋UIAutomationClient，见 R-D9）逐个替换 PowerShell 窗口脚本，补齐目前没有窗口测试的对话框与流程；⑤ 每个已修复问题一条回归用例，整理回植清单；⑥ 集成测试运行时间。

**进度（2026-11-02，R7 第①块）：** `docs/TEST-COVERAGE.md` 更新到 R3–R6 之后的测试：第 4 列由“仍覆盖的旧场景”（旧运行器已无场景）改为“窗口与其他检查”，各行补上 R4–R6 新增的 Presentation／Application 测试与覆盖它的窗口脚本，状态改为完成／部分／待办并写明缺口。新增 `Inventory.CoverageMatrixTests`：读 3.1–3.3 节，要求每行至少一条存在的测试、写到的每个类／方法／通配名称／窗口脚本都存在；F26（打包检查）、I14（性能基准）、J07（日志保存基准）目前只有命令行检查，列为已知例外，有了测试就必须移出。检查当场发现 J01 引用的方法已改名。回植 6 种（改名方法、缺失类、无效通配、缺失脚本、整行无测试、例外名单漏项）均被抓到。现为“部分”的行：F04 冲突审阅、F10 参数选择、F11 导入、F14 提交移交缺窗口测试，F15／I01 缺写入状态表。

**进度（2026-11-02，R7 第②块）：** `tests/VamSys.Tests` 拆除。种子、重启断言、打包检查与诊断回放移到 `tools/VamSys.QaSeed`；基准移到 `tests/VamSys.Benchmarks`（只测量、写 JSON）；两者共用的存储夹具、窗口数据夹具和旧式 HTTP 桩移入 `tests/VamSys.TestSupport`（公开类型），重复的虚拟时钟删除，单元测试不再链接 `DataUiFixture.cs` 源文件。12 个窗口脚本、`build.ps1`、CI、解决方案与各说明文档改指新项目；测试说明集中到新的 `tests/README.md`。CI 原先的“Legacy suite”一步不带参数运行旧运行器、以退出码 2 结束，已改为构建两个新项目。打包检查补上测试 `IntegrationTests.Packaging.PackageChecksTests`（19 例）：用 `MetadataBuilder` 生成只含元数据的程序集拼出正式包与 QA 包，各通过一次，再逐项破坏、核对拒绝消息；对检查本身回植 6 种错误均被抓到。检查也改为拒绝所有测试与工具程序集（原先只认 `VamSys.Tests`）。F26 移出覆盖矩阵的例外名单，剩 I14、J07。分析器基线条目随项目改名，总数仍 47。Full 检查（单元 1618、集成 503）与两个包通过；`--seed-storage-ui`、`--benchmark-storage` 实际运行正常。窗口验收（2026-11-02，952716a 的包）：用到种子或重启断言的 12 个窗口脚本全部通过——`test-v3-verification-ui` 默认／`-Autosave`／`-History`／`-Tasks`、`test-storage-ui`（含旧库迁移、第二实例拒绝、未来版本拒绝）、`test-recovery-ui`、`test-credentials-ui`、`test-cell-editing-ui`、`test-data-footer-ui`、`test-ui-interactions -Rows 20000`、`test-data-performance-ui -Rows 20000`（打开 311 ms，搜索 101 ms，翻页 P95 8.3 ms）、`test-api-ui-20k`，没有残留进程。

### R8 验收与收尾

1. 全部单元、集成和窗口测试，正式包与 QA 包检查；需求覆盖矩阵每一行都有通过的测试，回植错误全部被发现；旧运行器和旧脚本已删除。
2. 用真实数据库副本重复 R0 第 8 步，计数、内容键和 Pending 任务一致。
3. 性能报告前后对比；更新 README、各项目 README、测试报告、性能报告、更新日志，把本计划完成部分并入 [代码优化计划](OPTIMIZATION-PLAN.md)。
4. 按现有规则归档旧正式包和本轮产物。

**不包括：** 软件真实写入验收，仍需另行授权。

## 6. 与现有路线图的关系

| 审查项 | 处理方式 |
|---|---|
| C1–C6、B1–B10、A5–A7，原计划第 1–6 项 | **由重构完成**，分别落在 R1–R7 |
| D2（CI） | 并入 R0 |
| D3（脚本编码与 `test-localization-ui` 失败） | 并入 R7 第 4 步（窗口测试改为 C# 后编码问题自然消失）；R6 开始前先修好旧脚本的失败，保证界面重构期间有可用的本地化验收 |
| A2、A4 | 小型行为修复，**建议在 R0 之后、R2 之前**单独完成（有失败测试即可），以免重构期间带着已知错误搬代码 |
| A3 | 等决定 #1；拿到决定后同 A2 一起做 |
| F7 | 只改文档和提示，随时可做 |
| E1–E10、F1–F9（F1 已完成）、G3–G8 | 行为变化，**重构期间冻结**（P1 修复除外）。R3 完成后，F6 可以直接复用变更日志；R5 完成后，E、G 系列在新的流水线上实现会更简单 |
| D4 窗口验收 | 并入 R6、R8 |

## 7. 工作方式

- **分支与标签。** 每个阶段一个分支，结束时打标签。行为修复用独立提交并在说明中标出，方便回退。
- **小步。** 一次只移动一个服务、一个页面或一类资源；不允许“改结构同时改逻辑”的提交。
- **随时可退。** 存储格式不变是硬约束，所以任何阶段结束后的程序都能和上一版程序交替打开同一个数据库。如果将来必须改格式，单独决定，并补迁移测试。
- **证据。** 每阶段记录：回归数、新增测试、金样是否变化（应为否）、性能前后数字、运行过的窗口脚本。写入测试报告。
- **已有任务。** 用户本机现存的 Pending 任务在任何阶段都不自动运行；真实数据只在副本上做只读比对。

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| 搬代码时改变了请求或保存顺序 | 请求金样、存储金样逐字节比较；进程终止测试每阶段都跑 |
| 枚举重排、资源键改名，导致旧任务读出来意思变了 | I09、I10 清单测试；改名只能新增键，旧键保留 |
| XAML 化后 AutomationId 变化，窗口脚本失效 | I12 清单测试；页面逐个迁移、逐个跑脚本 |
| 数据页性能退化 | 数据表控件原样保留；50 k 脚本每次迁移后测量 |
| 变更日志漏记某个修改入口，增量保存丢数据 | R2 先禁止原地修改并加断言；随机操作与完整保存逐表对照；保留“失败回退到完整保存” |
| 重构周期长，与功能开发冲突 | 第 6 节的冻结规则；阶段之间可以发布 |
| 本机没有 git，无法做小步提交和回退 | R0 第 1 步先解决 |
| 测试和产品代码同时重构，安全网和被测对象一起变 | 金样和清单测试只看输入输出，整个重构期间不改；某层旧测试只在该层新测试按需求覆盖矩阵全部通过后删除 |
| 旧测试中隐含、但文档没写的行为在重建时漏掉 | 删除某层旧测试前，逐个读一遍它的断言，没有对应需求的先补进第 3 节再写新测试；已修复问题清单逐条写回归 |
| 数据表重写后性能或键盘行为退化 | QA 构建中新旧控件可切换并排测量；未达标前正式包不切换 |
| 窗口测试重写期间失去界面验收 | 某页面的旧脚本，在该页面重构完成、新窗口测试通过后才删除 |
| 存储层重写后旧版程序无法打开新库 | 文件格式不变；`SchemaV4` 与现有库逐条对照；每阶段用旧程序集打开新库 |

## 9. 需要决定的事项

| # | 事项 | 建议 |
|---|---|---|
| R-D1 | 渐进重构还是重写 | 渐进（理由见开头） |
| R-D2 | 项目和命名空间是否改名（如 `VamSys.*` → `FlightOps.*`） | 不改，沿用 `VamSys.` 前缀；EXE 和品牌已是 FlightOps Desk |
| R-D3 | 界面重构的范围 | 全部重写，包括数据表；数据表在 QA 构建中新旧并行，达到性能门槛后切换 |
| R-D4 | 测试框架与旧测试 | 新测试用 xUnit v3（可执行测试项目，兼容子进程恢复测试），按需求重新编写；旧测试不迁移，逐层删除；种子与断言入口重写为独立工具 |
| R-D5 | 变更日志是否覆盖撤销历史（即原决定 #2） | 只覆盖行和元数据，撤销历史沿用已有增量机制 |
| R-D6 | 演示是否改为共用在线执行流水线（C3） | 改，但作为 R5 之后的独立行为变更；在此之前文档写明差异 |
| R-D7 | 重构期间是否冻结 E／F／G 功能项 | 冻结，P1 修复除外 |
| R-D8 | git 的安装方式 | 安装 Git for Windows；不方便时用便携版放入 `.tools` |
| R-D9 | 窗口测试的实现方式 | 改为 C# 驱动 QA 包。原定 FlaUI；2026-11-02 改为直接用 Windows 自带的 UIAutomationClient（现有脚本已在用），不新增 NuGet 依赖、不需下载，代价是自写一层小封装 |
| R-D10 | 界面视觉是否允许调整 | 只允许统一间距、字号和样式等整理；不增删功能入口，不改文案和 AutomationId |

[审查与优化计划](REVIEW-AND-PLAN-2026-10-20.md) · [代码优化计划](OPTIMIZATION-PLAN.md) · [能力验证矩阵](CAPABILITY-MATRIX.md) · [项目首页](../README.md)
