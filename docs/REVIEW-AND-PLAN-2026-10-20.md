# 全面代码审查与优化计划（2026-10-20）

本文是对 FlightOps Desk 源码的一次完整审查，以及据此制定的分阶段优化计划。它补充而不替代 [代码优化计划](OPTIMIZATION-PLAN.md)：那里的“不能改变的要求”和“明确不做的优化”在这里全部继续有效，原有“后续方向”第 1–7 项已并入文末路线图。

> **2026-11-08 状态：** 阶段 1 已完成；A、B、C 系列和原计划第 1–6 项已由重构 R0–R8 及此前的修复完成。D 系列还剩两处：云端 CI 尚未运行，7 个连接后才出现的对话框仍没有窗口覆盖。重构期间冻结的 E1–E10、F2–F9、G3–G8 已解冻，执行顺序改以 [综合优化计划（2026-11-08）](IMPROVEMENT-PLAN-2026-11-08.md) 为准；本文各项的详细说明继续有效。

**阅读方式：** A–D 是代码审查发现，E–G 是针对 API、提交流程和自动核对的专项分析；各节表格内的“优先级”只在本节内比较。**实际执行顺序以文末 [路线图](#路线图) 为准**，其后是需要决定和需要真实验证的事项。

## 审查范围与方法

- 逐文件阅读了 `src` 下全部 C# 源码（Core 约 1,300 行、Infrastructure 约 2,300 行、App 约 2,700 行），并对照了 README、性能报告、测试报告和现有优化计划。
- 在本机运行全部自动测试作为基线：**260 项通过，耗时 30.7 s**（`.tools/dotnet` 10.0.401，Release）。
- 未运行窗口脚本，未访问真实 API，未修改任何源码。下面标“推断”的条目由代码直接推出，尚未用测试复现；修复时应先补一条失败的测试。
- 本机 shell 中没有 `git`，未核对提交历史。

## 总体评价

**做得好的地方：** 写入安全性设计非常严谨——写入前后持久化、未知写入只读核对不重发、版本号防覆盖、检查点故障注入与真实进程终止测试；判重、限流和契约校验都有回归测试覆盖；性能工作有测量、有前后对比、有归档。

**主要短板：**

1. **持久化粒度过粗。** “除单元格编辑外，任何改动后立即完整保存”的约定没有在代码中强制，且让很多本可以只写几行的操作都要序列化并重写全部行（5 万行约 2 s，在界面线程上序列化）。这是大部分界面卡顿的共同根源。
2. **界面线程上的同步 I/O。** 多处为了读一个任务或检查一个任务是否存在而完整读取工作区（5 万行约 1.1 s，分配约 530 MB）。
3. **`MainWindow` 承担过多职责。** 15 个分部文件、约 2,000 行，混合了页面构建、状态、持久化策略和业务逻辑（例如演示任务的回填）。
4. **代码密度过高。** 大量 200 字符以上的单行、多条语句挤在一行、格式不统一，审查和定位问题的成本很高。

## 问题清单

严重度：**P1** 可能导致功能不可用或数据处理错误；**P2** 行为不一致、明显的性能问题或隐患；**P3** 可维护性和整洁度。

### A. 正确性与健壮性

| # | 严重度 | 位置 | 问题 | 建议 |
|---|---|---|---|---|
| A1 | P1（推断） | [SnapshotMerger.cs:9](../src/VamSys.Core/SnapshotMerger.cs) | 机场别名用 `ToDictionary` 建立。两个远端机场只要共用同一个 IATA（重复录入、代码复用），就会抛出未本地化的 `ArgumentException`，该 VA 的“从 API 刷新”整体失败且无法自行恢复。`AirportIndex()` 和 `DeletionService` 对同样情况都能处理，三处规则不一致。 | 别名映射到集合；多义别名不用于匹配本地无身份行，并在冲突列表中提示。补“两机场同 IATA”刷新测试。 |
| A2 | P2 | [OperationsBatchExecutor.cs:81-82](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)、[OperationsAdapter.cs:146,172,177](../src/VamSys.Infrastructure/OperationsAdapter.cs)、[OperationsMovement.cs:15-20](../src/VamSys.Infrastructure/OperationsMovement.cs) | `local:` 引用的识别规则不统一：`ChangePlanner` 和 `CsvAdapter` 不区分大小写，执行器和适配器用无比较参数的 `StartsWith("local:")`（区分大小写、依赖区域性）和 `==`。CSV 中写成 `LOCAL:…` 时，规划阶段建立了依赖，执行阶段 `Single()` 找不到匹配，抛出未本地化异常，条目变为 Failed。 | 导入时把 `local:` 令牌规范化为小写；新增唯一的 `LocalReference.TryParse` 供所有位置使用，统一 `StringComparison.OrdinalIgnoreCase`。 |
| A3 | P2 | [MainWindow.Fleets.cs:55](../src/VamSys.App/MainWindow.Fleets.cs) 与 [BatchEditPreview.cs:30](../src/VamSys.App/BatchEditPreview.cs)、[ChangePlanner.cs:80](../src/VamSys.Core/ChangePlanner.cs) | “停用航线”有两条入口，结束日期不同：工具栏命令用当前 UTC −2 天，批量编辑里的“停用”规则用 −1 天。 | 需要确定哪个是预期值，再合并为一个常量和一条代码路径。 |
| A4 | P2 | [ChangePlanner.cs:78-79](../src/VamSys.Core/ChangePlanner.cs) | 批量“平移分钟”跨午夜时 `TimeOnly` 会静默回绕，不调整日期或到达时间；选中行里只要有一行时间为空或格式不对，整个预览抛出原始 `FormatException`；`int.Parse` 对非数字输入同样直接抛出。 | 先校验规则输入；逐行判断，不能处理的行计入“跳过”并给出原因；跨午夜时明确提示。 |
| A5 | P2 | [Models.cs:50](../src/VamSys.Core/Models.cs) 等 | `ShallowCopy` 的注释要求“只替换行，不原地修改”，但单元格编辑、CSV 合并导入、删除标记／恢复、演示回填都原地修改行（[MainWindow.Data.cs:242](../src/VamSys.App/MainWindow.Data.cs)、[CsvAdapter.cs:88](../src/VamSys.Core/CsvAdapter.cs)、[DeletionService.cs:58,65](../src/VamSys.Core/DeletionService.cs)、[MainWindow.xaml.cs:376-378](../src/VamSys.App/MainWindow.xaml.cs)）。目前只因为刷新期间界面被锁定才是安全的。 | 二选一并写明：要么所有修改都改为替换行（与 `ReplaceDraftRows` 一致），要么把“暂存期间禁止编辑”作为显式约束并加断言。 |
| A6 | P3 | [App.xaml.cs:11-18](../src/VamSys.App/App.xaml.cs)、[MainWindow.Localization.cs:25](../src/VamSys.App/MainWindow.Localization.cs) | 未处理异常只记日志；`startup-error.log` 和 `localization.log` 无限增长。 | 日志按大小轮换；未处理异常记录后提示用户数据已保存的状态。 |
| A7 | P3 | 多处 | 无用代码：`Workspace.InstanceUrl` 只保存不使用（地址固定为 `BaseUri`）；`DraftEdits.Cell` 的 `checkpointed` 参数未使用；`!closeState.IsOpen \|\| busy \|\| !closeState.IsOpen` 重复条件出现 5 次（[MainWindow.xaml.cs](../src/VamSys.App/MainWindow.xaml.cs) 192、197、311、340、395 行）；[WorkspaceStore.Schema.cs:65](../src/VamSys.Infrastructure/WorkspaceStore.Schema.cs) 的 `if(version<2)WriteJobs(...);WriteResources(...)` 写在一行，容易误读为两者都受条件控制。 | 清理并补充花括号；`InstanceUrl` 保留字段以兼容旧数据，但从界面移除或标为保留。 |

### B. 性能（现有计划之外的新发现）

| # | 严重度 | 位置 | 问题 | 建议 |
|---|---|---|---|---|
| B1 | P2 | [MainWindow.xaml.cs:231](../src/VamSys.App/MainWindow.xaml.cs)、[MainWindow.Batch.cs:96,118](../src/VamSys.App/MainWindow.Batch.cs)、[MainWindow.Fleets.cs:47,59](../src/VamSys.App/MainWindow.Fleets.cs)、[MainWindow.Maintenance.cs:34](../src/VamSys.App/MainWindow.Maintenance.cs) | 新增一行、批量编辑、改机队、停用、恢复删除、选择批准参数，每次都完整保存。其中除“新增行”外都是**原地替换**，完全可以走撤销／重做已经在用的行级保存路径（`edits.Rows` + `SaveEdits`）。 | 原地替换的操作改为行级保存；新增行用“追加一行”的增量写入。 |
| B2 | P2 | [MainWindow.Api.cs:74,86](../src/VamSys.App/MainWindow.Api.cs) | 冲突处理每解决一个冲突就完整保存一次。刷新后有几百个冲突时，每次点击都要等约 2 s。 | 只修改内存，对话结束（或中途取消）时保存一次；冲突列表本身也可增量保存。 |
| B3 | P2 | [MainWindow.Submission.cs:11](../src/VamSys.App/MainWindow.Submission.cs)、[MainWindow.Tasks.cs:38](../src/VamSys.App/MainWindow.Tasks.cs)、[MainWindow.Api.cs:34](../src/VamSys.App/MainWindow.Api.cs)、[MainWindow.xaml.cs:361](../src/VamSys.App/MainWindow.xaml.cs) | `Save(dataChanged:false)` 只是不让数据页失效，**仍然完整保存**。提交任务、重试、连接 API 都会重写全部行；演示任务每处理一条就完整保存一次。 | 新增“只写工作区头部 JSON”“插入任务”“更新任务状态”的存储方法；演示执行器改用检查点写入器。 |
| B4 | P2 | [MainWindow.Submission.cs:18](../src/VamSys.App/MainWindow.Submission.cs)、[MainWindow.Diagnostics.cs:12](../src/VamSys.App/MainWindow.Diagnostics.cs)、[WorkspaceStore.Maintenance.cs:20](../src/VamSys.Infrastructure/WorkspaceStore.Maintenance.cs) | 为确认一个任务已保存、导出一个任务的诊断、显示维护预览，都在界面线程上完整读取工作区。 | 新增 `JobExists`、`LoadJob`、`CountRows` 等定向查询。 |
| B5 | P2 | [WorkspaceStore.cs:113-118](../src/VamSys.Infrastructure/WorkspaceStore.cs)、[MainWindow.xaml.cs:397](../src/VamSys.App/MainWindow.xaml.cs) | 保存连接设置时，先把整个工作区序列化再反序列化（会重新校验全部撤销历史），再同步完整保存——全部在界面线程上。5 万行时点“保存”会冻结数秒。 | 只写工作区头部和密文，放到后台执行。 |
| B6 | P2 | [MainWindow.xaml.cs:290-297](../src/VamSys.App/MainWindow.xaml.cs) | 在线模式下的“检查”在界面线程上规划整个工作区并逐条预演。 | 与审阅页一样放到后台的 `WhileLocked` 中。 |
| B7 | P3 | [OperationsBatchExecutor.cs:32,40,71,82](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)、[BatchExecutor.cs:24,54](../src/VamSys.Core/BatchExecutor.cs)、[MainWindow.xaml.cs:366](../src/VamSys.App/MainWindow.xaml.cs) | 执行器中按依赖和令牌线性查找条目、每条 `Snapshot.RemoveAll`、每条用 JSON 往返深拷贝，整体是 O(n²)。在线时被限流掩盖，演示和大任务恢复时会显现。 | 执行开始时建立 Id／LocalId 字典；快照按位置索引删除。 |
| B8 | P3 | [Schemas.cs:25](../src/VamSys.Core/Schemas.cs)、[OperationsAdapter.cs:203-230,337-344](../src/VamSys.Infrastructure/OperationsAdapter.cs)、[OperationsQueries.cs:23](../src/VamSys.Infrastructure/OperationsQueries.cs) | 同一行的 `RawApiJson` 在别名、端点、时间秒数、签名等处被反复解析。 | 按行缓存解析出的少量字段（别名、端点 ID、原始时间）。 |
| B9 | P3 | [Schemas.cs:41-133](../src/VamSys.Core/Schemas.cs) | 校验每个字段都新建数组再 `Contains`，`Key()`（Trim + ToUpper）对同一行重复计算，每次都为所有资源重建引用键集合。提交、导入、演示审阅都会调用。 | 静态只读集合；每行键只算一次。 |
| B10 | P3 | [OperationsTransport.cs:55](../src/VamSys.Infrastructure/OperationsTransport.cs) | `HttpClientHandler` 没有连接寿命限制，长会话不会刷新 DNS。 | 改用 `SocketsHttpHandler` 并设置 `PooledConnectionLifetime`。 |

### C. 架构与可维护性

| # | 问题 | 建议 |
|---|---|---|
| C1 | “非单元格改动后立即完整保存”只是约定，没有机制保证。现有计划第 1 项（切换工作区只写待保存的编辑）因此一直不敢做；关闭窗口也总是完整保存。 | 在 `ResourceData` 中加入**变更日志**：记录被替换／新增／删除的行、顺序是否变化、列和冲突是否变化。存储根据日志增量写入，日志为空就不写。这是 B1–B3、现有计划第 1 和第 4 项的共同根本解法。 |
| C2 | `MainWindow` 同时负责页面、状态、保存策略和业务逻辑；演示任务的快照回填写在界面代码里（[MainWindow.xaml.cs:363-382](../src/VamSys.App/MainWindow.xaml.cs)）。 | 逐步抽出 `WorkspaceSession`（持有工作区、保存锁、变更日志和保存策略）和 `DemoRunner`；页面只做展示。每次抽一块，行为不变。 |
| C3 | 两个执行器语义不同：演示用 Core 的 `BatchExecutor`（每条完整保存、无步骤检查点），在线用 `OperationsBatchExecutor`。演示无法代表在线流程的恢复行为。 | 让演示服务实现在线执行器所需的接口，或至少共享检查点写入；短期内在文档中写明差异。 |
| C4 | 字段规格分散在 `Schemas`、`OperationsAdapter.Maps`、`Validate`、`IndependentApiEvidence`、`Equivalent` 等至少 6 处（现有计划第 6 项）。 | 建立统一的字段规格表（CSV 名、线上名、类型、是否可清空、集合比较），各处从表中读取。 |
| C5 | 数据目录路径 `VAMSYS_DATA_DIR / LocalAppData\VamSysBatch` 在 5 处重复拼写。 | 抽成一个 `AppPaths.DataDirectory`。 |
| C6 | 代码密度与格式：大量超长单行和多语句同行，文件之间风格不一；没有启用代码分析器。 | 增加 `.editorconfig`，开启字符串比较（CA1307/CA1309/CA1310）和释放（CA2000）等分析规则；只格式化本次修改的文件，避免一次性大改造成无法审查的差异。 |

### D. 测试与工程

| # | 问题 | 建议 |
|---|---|---|
| D1 | A1–A4，以及 E–G 中的行为缺陷（F1、G1、G2、G7）都没有对应测试。 | 每项修复前先补一条能复现问题的失败测试，修复后再回植旧逻辑确认测试会失败。 |
| D2 | 仓库中没有持续集成配置。 | 在 Windows 运行器上执行全部自动测试和正式包检查。 |
| D3 | `build.ps1` 和多个窗口脚本是无 BOM 的 UTF-8，Windows PowerShell 5.1 无法运行；`test-localization-ui.ps1` 在“Apply to drafts”处失败（现有计划第 7 项）。 | 改为带 BOM 的 UTF-8 或去掉中文字面量；单独修复脚本。 |
| D4 | 新增界面（API、参数、jumpseat、诊断导出、维护）没有窗口验收；单元格新外观没有在高对比度下检查。 | 现有界面在路线图最后一阶段统一补做；本计划新增或修改的界面（F1、F2、F3、G1、G2）在各自完成时验收。 |

## E. API 逻辑优化

在“改前读取最新状态、改后回读、请求间隔至少 1 秒、不并行、不自动重发”的约束下，一次修改至少需要 3 次请求（GET、写入、回读 GET），现有执行器已经是这个下限。剩余空间在刷新、判重、删除检查和“结果未知”的处理上。下表按优先级排列。

“验证”一列标 **需真实 GET** 的，必须先用真实账号做只读请求确认服务器行为，再开始实现；标 **需决定** 的，与现有规则或[“明确不做”清单](OPTIMIZATION-PLAN.md#明确不做的优化)的字面冲突，需要先确定是否修改规则。

| 优先级 | # | 位置 | 内容 | 预期收益 | 验证 |
|---|---|---|---|---|---|
| 1 | E1 | [OperationsBatchExecutor.cs:115](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)、[OperationsUnknownResolution.cs](../src/VamSys.Infrastructure/OperationsUnknownResolution.cs)、[MainWindow.Api.cs:159-171](../src/VamSys.App/MainWindow.Api.cs) | **结果未知的新建自动找回记录。** 现在超时或 5xx 后没拿到 ID 的新建，只能由用户去 vaMSYS 查 Remote ID 手动填入；代码只能“证明未生效”，不能自动关联已创建的记录。做法：所有新建（不只 jumpseat）在 POST 前记录当时已有的候选 ID（判重时本来就遍历这些候选，几乎没有额外成本）；核对时若恰有一条新出现、内容与发送值一致、`created_at` 落在请求时间窗内的记录，自动作为候选交给用户确认。全程只读。 | 大部分未知新建无需人工查 ID | 无需真实请求即可实现；时间窗判定沿用 jumpseat 已有规则，上线前用模拟服务器覆盖 0／1／多条候选 |
| 2 | E2 | [OperationsBatchExecutor.cs:180-201](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs) | **每轮结束时自动只读核对一次未知项。** 写入超时或 5xx 后条目停在“未知”，要等用户点“继续”才会 GET 核对。改为本轮所有条目处理完后，对本轮新产生的未知项各做一次只读核对（复用现有恢复路径，不发写入）。 | 减少一次人工操作；能确认的条目当轮结束 | 无需真实请求；需测试核对 GET 失败时状态不变 |
| 3 | E3 | [OperationsTransport.cs:55](../src/VamSys.Infrastructure/OperationsTransport.cs)、[OperationsAdapter.cs:427-428](../src/VamSys.Infrastructure/OperationsAdapter.cs) | **读写分开设置超时。** 现在读写都是 30 秒；读超时会自动重试，代价小，写超时直接变成“未知”，代价大。写入改为 60–90 秒，读取保持 30 秒。jumpseat 的 `DeadlineAt` 时间窗依赖同一个超时值，要一起调整并保持旧任务按其保存的截止时间判定。 | 减少慢响应造成的假“未知” | **需决定**具体时长 |
| 4 | E4 | [OperationsTransport.cs:126](../src/VamSys.Infrastructure/OperationsTransport.cs) | **刷新时加大每页条数。** 现在每页 100 条；契约未写上限，代码已能在服务器拒绝页大小（400／422）时退回默认值。以测试报告中的真实 vCCA 数据（239 机场、37 机队、579 飞机、15 航路、3,628 航线）估算，每页 500 条时完整刷新约从 83 次 GET 降到约 48 次（航线 37 页 → 8 页；飞机只能逐机队读取，37 次不变），约少 35 秒。 | 完整刷新约少 40% 请求 | **需真实 GET**：确认服务器按 500 返回而不是静默截断，以及游标翻页在大页下仍连续、不丢不重 |
| 5 | E5 | [OperationsAdapter.cs:104-136](../src/VamSys.Infrastructure/OperationsAdapter.cs) | **按资源刷新。** 现在一律读取全部五类资源。只处理航线时，只需读机场（把机场 ID 换回代码）、机队和航线；结合 E4，约 10 次 GET。合并本来就按资源进行；仍然一次性原子保存。注意：刷新完成时会用本次读取结果整体替换身份索引（[OperationsAdapter.cs:134](../src/VamSys.Infrastructure/OperationsAdapter.cs)），部分刷新必须改为只替换所读资源的条目，否则未读资源的机场和机队解析会丢失。界面需说明其他资源的快照时间未更新。 | 只改航线时刷新从约 83 次降到约 10 次 | 依赖 E4 的验证结果；需补测试：部分刷新不改动其他资源的快照、草稿、冲突和身份索引 |
| 6 | E6 | [OperationsAdapter.cs:104-136](../src/VamSys.Infrastructure/OperationsAdapter.cs) | **刷新失败后只重读失败的资源。** 刷新是原子的，读到第 80 次请求时失败，前面全部作废。改为在同一会话内保留已完整读取的资源结果，重试时只读失败的那一类，最后仍一起保存。 | 网络不稳时避免整轮重读 | **需决定**：已读结果允许保留多久（建议仅限同一会话、几分钟内），超时即全部重读 |
| 7 | E7 | [OperationsBatchExecutor.cs:186-193](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs) | **被限流（429）或请求未发出的写入，在本轮稍后再发一次。** 这两种情况服务器肯定没有处理，现在条目退回“待执行”，要等用户再点“继续”。本轮按 Retry-After 等待后再发一次是安全的。 | 少一次人工“继续” | **需决定**：字面上违反“写入失败后不自动重试”；若采纳，规则中应写明只限 429 和“请求未发出”，且每条最多一次 |
| 8 | E8 | [OperationsQueries.cs:114-121](../src/VamSys.Infrastructure/OperationsQueries.cs) | **新建机场或机队时按数量选择判重方式。** 现在先读完整列表建索引。契约支持 `filter[icao]`、`filter[iata]`（机场）和 `filter[name]`（机队）。新建条数少而列表很长时，按条件查询（机场每条 2 次）更省；条数多时继续用索引。飞机的注册号筛选只能逐机队进行，继续用索引。客户端的 `IsDuplicate` 判定保持不变。 | 大型 VA 少量新建时少读若干页 | **需真实 GET**：确认筛选是否忽略大小写、是精确还是包含匹配。若区分大小写或精确匹配可能漏掉重复，则不采用 |
| 9 | E9 | [OperationsBatchExecutor.cs:26,60](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)、[OperationsParameters.cs:12](../src/VamSys.Infrastructure/OperationsParameters.cs) | **新建机队不再每条重读批准参数。** 每完成一个机队条目就清空参数和已检查机队的缓存，连续新建 N 个机队会重读 N 次参数列表。新建机队不会改变参数的批准状态，只需把“机队与参数的关联”标为过期，在第一条需要前缀检查的航线前重读一次。 | 新建 N 个机队少 N−1 次参数读取（批量新建机队少见，收益小） | **需决定**：同一轮内是否接受用轮次开始时读取的批准状态（外部在执行中途撤销批准的情况会晚一轮发现） |
| 10 | E10 | [OperationsTransport.cs:28](../src/VamSys.Infrastructure/OperationsTransport.cs) | **OAuth 只申请需要的权限。** 现在申请 `scope="*"`。这是安全项，不影响性能。 | 最小权限 | **需真实请求**：确认 vaMSYS 是否支持更细的 scope、各 scope 名称，以及只用 GET 和取令牌即可验证 |

**不建议做：** 把限流放宽到每分钟 100 次或并行发请求（原因见现有计划）；删除机场前一次性读取全部航线和航路，代替每条 4 次依赖查询——这与“不提前批量读取做冲突检查”是同一类做法，数据可能过时。

E1、E3 与 G5 都涉及 jumpseat 和新建恢复的时间窗口，应作为一组一起修改。每项都要通过全部自动测试和真实进程终止恢复测试，并记录修改前后的请求次数。排期见 [路线图](#路线图)。

## F. 改动提交流程

当前流程：连接 → 审阅页在后台生成计划 → 点“提交”时再生成一次计划并比对内容 → 确认清单（删除另确认一次）→ 完整保存任务 → 执行（期间整个程序锁定）→ 任务页处理未知项。下列各项都不改变写入安全规则（写前读最新、写后回读、不重发）；标 **需决定** 的会改变现有的保守策略或使用方式。

| 优先级 | # | 位置 | 内容 | 完成标准 | 决定 |
|---|---|---|---|---|---|
| 1 | F1 | [MainWindow.Api.cs:45](../src/VamSys.App/MainWindow.Api.cs)、[MainWindow.Review.cs:31-32](../src/VamSys.App/MainWindow.Review.cs)、[WorkspaceStore.cs:88](../src/VamSys.Infrastructure/WorkspaceStore.cs)、[MainWindow.Tasks.cs](../src/VamSys.App/MainWindow.Tasks.cs) | **放弃未发出的“待执行”项。** 任务暂停后剩下的待执行项只能“继续”执行，没有放弃入口；只要存在，刷新、提交新任务和保存连接设置都被拦住，唯一出路是备份并清空工作区。新增“放弃未发出的项”：只对所有步骤仍为“已准备”、`CreateCompleted` 和 `WriteAccepted` 均为假的项可用，标为**失败**并附专用说明（如“已放弃，未发出”），草稿保留，可重新审阅。**不能标为“跳过”**：执行器每次“继续”都会重新处理跳过项（[OperationsBatchExecutor.cs:70](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)），放弃会被撤销。用“失败”而不是新增状态，是因为两个执行器和已发布的旧版本都会跳过失败项，回退到旧版本也不会把它发出去；`OperationsUnknownResolver` 的“证明未生效”已经这样做。用检查点写入器先保存再改内存。 | 测试：放弃后刷新和提交不再被拦；再次“继续”不会发出已放弃的项；有任何步骤曾发送中、已应答或已接受的项不能放弃；保存失败时内存不变；任务页能区分“已放弃”和真正失败 | 无需决定（这些项从未到达服务器） |
| 2 | F2 | [MainWindow.xaml.cs:114-122](../src/VamSys.App/MainWindow.xaml.cs)、[MainWindow.Data.cs:205,392-399](../src/VamSys.App/MainWindow.Data.cs) | **执行期间允许只读浏览。** 执行可能持续很久（每秒 1 次请求，修改至少 3 次请求，1,000 条约 50 分钟），期间数据表整个屏蔽输入，连滚动、翻页和搜索都不行。改为执行期间可以翻页、搜索、滚动和查看审阅页，只禁止编辑、批量操作、切换工作区和再次提交。查询读取的是任务回填前后的草稿，需保证回填在界面线程上完成、查询结果失效后重新查询。 | 窗口脚本：执行中可滚动、翻页、搜索；所有编辑入口不可用；任务回填后当前页数据正确 | 无 |
| 3 | F3 | [MainWindow.Review.cs:170-184](../src/VamSys.App/MainWindow.Review.cs) | **确认对话框改为汇总，并显示预计耗时。** 现在为每条变更生成一行文字（5 万条即 5 万行），删除再单独列一次。改为按资源和操作类型列数量，明细可展开；显示预计请求次数和耗时（修改约 3 次，航线／航路新建约 2–3 次，删除机场约 7 次，删除机队约 5 次，按每秒 1 次计算；判重索引和批准参数读取按实际是否需要计入）。删除数量在同一对话框中突出显示，保留单独确认。 | 5 万项变更时对话框打开时间有前后对比；预计次数与模拟服务器实际请求数的偏差在 10% 以内 | 无 |
| 4 | F4 | [ReviewPlanBuilder.cs:110-117](../src/VamSys.App/ReviewPlanBuilder.cs)、[MainWindow.Tasks.cs](../src/VamSys.App/MainWindow.Tasks.cs) | **样本通过后直接提交剩余项。** 未验证的操作类型每种只放行 3 条；样本成功后用户要自己回审阅页、重新生成计划、再提交。改为任务结束且样本全部成功时，在任务页显示“继续提交剩余 N 条”，点击后重新生成计划并走同一确认流程。 | 测试：样本有失败或未知时不显示；按钮生成的任务与从审阅页提交的内容一致 | 无 |
| 5 | F5 | [MainWindow.Review.cs:33-40](../src/VamSys.App/MainWindow.Review.cs) | **在线模式审阅时运行本地校验。** 现在只有演示模式在审阅时运行 `Schemas.Validate`；在线模式只靠接口规则预检，草稿间重复主键、可能重复的新建、指向已删除记录的引用要到执行时才变成失败或冲突。在线模式也在审阅页显示这些问题。 | 测试：上述三类问题在审阅页可见 | **需决定**：只提示，还是像演示模式一样阻止提交（建议先只提示，相关条目标为受阻） |
| 6 | F6 | [MainWindow.Review.cs:160-169](../src/VamSys.App/MainWindow.Review.cs) | **提交前不必总是重建计划。** 点“提交”时会在后台再生成一次计划并比较内容键，5 万项变更约 1.2 s。给工作区加一个内存修改计数（所有编辑、导入、刷新、任务回填都递增），自生成计划以来计数、模式、连接和已验证操作都未变时跳过重建；否则仍重建并比对。 | 测试：任何修改入口都会让计数变化（遗漏即测试失败）；未修改时提交不再重建 | 无 |
| 7 | F7 | [README.md](../README.md)、[USER-GUIDE.md](USER-GUIDE.md)、任务完成提示 | **说明任务结束后不必为确认结果而刷新。** 每条成功的改动都已回读确认并更新了本地快照。把流程说明中的“提交 → 再次读取，确认结果”改为“只在需要获取他人修改时刷新”，可省掉一次完整刷新（约 80 次请求）。 | 文档和任务完成提示已更新 | 无 |
| 8 | F8 | [ReviewPlanBuilder.cs:132-135](../src/VamSys.App/ReviewPlanBuilder.cs)、[MainWindow.Review.cs:29-32](../src/VamSys.App/MainWindow.Review.cs) | **缩小“全局阻止”的范围。** 现在任何资源有一个冲突，或任何旧任务有一条待执行／未知项，所有可提交项都改为受阻。执行器已有逐条保护（未决写入的签名预留、写前冲突检查）。改为：冲突只阻止该行和依赖它的项；未知项只阻止同一记录、同一判重签名和依赖它的项。 | 测试：无关资源可以提交；与未知项同签名的新建、同一记录的修改仍被阻止 | **需决定**：放宽现有保守策略 |
| 9 | F9 | [ReviewPlanBuilder.cs](../src/VamSys.App/ReviewPlanBuilder.cs)、[MainWindow.Review.cs](../src/VamSys.App/MainWindow.Review.cs) | **允许只提交一部分。** 现在一次提交工作区里全部可提交项。增加按资源勾选，或只提交数据页中选中的行；依赖关系沿用现有“依赖暂缓”处理，未勾选的前置项使依赖项暂缓并说明原因。 | 测试：部分提交的任务只含所选项；依赖未选时暂缓而不是失败 | **需决定**：改变“一次提交全部”的使用方式；入口放在审阅页还是数据页 |

F6 的修改计数可以直接复用 C1 的变更日志，应排在 C1 之后。F1、F2、F3 涉及界面，完成后要做窗口验收。排期见 [路线图](#路线图)。

## G. API 自动核对逻辑

核对逻辑包括：写前冲突检查、写后回读比对、中断后的只读恢复、删除的 204／404 证明。未知项现在有四条出路：填写 Remote ID（未取得 ID 的新建）、人工确认删除（服务器已应答的软删除）、证明未生效（服务器未接受且不是软删除，[OperationsUnknownResolution.cs:44](../src/VamSys.Infrastructure/OperationsUnknownResolution.cs)）、自动删除核对（要求服务器已应答，[OperationsDeletionVerification.cs:16](../src/VamSys.Infrastructure/OperationsDeletionVerification.cs)）。下面 G1、G2 两种情况四条出路都走不通；而任何未知项都会挡住整个工作区的刷新、提交和维护清空（见 F8），所以这两项会让工作区卡死。

所有改动都只读、不重发写入。

| 优先级 | # | 位置 | 内容 | 完成标准 | 决定／验证 |
|---|---|---|---|---|---|
| 1 | G1 | [OperationsBatchExecutor.cs:104,149,152](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs)、[MainWindow.Api.cs:155-187](../src/VamSys.App/MainWindow.Api.cs) | **已接受但回读不一致的写入无法解除。** 包括三种情况：PUT 返回 2xx 但回读与发送值不一致（服务器规范化了值，或别人同时修改了记录）；新建已取得 ID 但回读不一致；机队、飞机或航路删除已接受但仍能读到记录。条目变为未知；之后每次“继续”都重新比对，依然不一致；审阅时只有说明框。唯一绕过办法是到 vaMSYS 网页把记录改成发送值。新增“接受服务器当前值”：先 GET 当前记录并列出差异（现有差异列表即可使用，G6 完成后原因更完整），用户确认后标为人工确认，本地快照更新为服务器当前值（删除的情况保留记录并撤销删除标记），草稿中尚未生效的部分作为冲突显示。人工确认是已有状态，旧版本也能识别。用检查点写入器先保存再改内存。 | 测试：三种情况都可以解除；解除后刷新和提交不再被拦；GET 失败或保存失败时状态不变；证据中保留差异和“人工接受”来源，不登记为已验证操作 | 无需决定 |
| 1 | G2 | [OperationsDeletionVerification.cs:7-29](../src/VamSys.Infrastructure/OperationsDeletionVerification.cs)、[OperationsUnknownResolution.cs:44-45](../src/VamSys.Infrastructure/OperationsUnknownResolution.cs)、[OperationsDeletionVerification.cs:42-47](../src/VamSys.Infrastructure/OperationsDeletionVerification.cs) | **机场或航线删除在超时／5xx 后无法解除。** 服务器没有应答，`WriteAccepted` 为假；自动核对结果为“缺少写入证据”，人工确认要求已接受，“证明未生效”排除了软删除，所以无论 GET 是 404 还是 200 都永远未知。分两种情况增加出路：**(a)** GET 返回 200 且与修改前一致 → 允许“证明未生效”（依据：已有证据显示软删除后读取返回 404）；**(b)** GET 返回 404 → 允许人工确认，证据注明“无写入回执”，与完整 204／404 证明区分，不登记为已验证操作。 | 测试：两种情况均可解除；GET 200 但与修改前不同时仍不可解除；(b) 的证据与完整证明可区分；原有完整证明不受影响 | **需决定** (a)：改变“软删除不能用读取证明”的现有规则 |
| 2 | G6 | [OperationsAdapter.cs:449-498](../src/VamSys.Infrastructure/OperationsAdapter.cs)、[OperationsParameters.cs:75-91](../src/VamSys.Infrastructure/OperationsParameters.cs)、[OperationsUnknownResolution.cs:14-19](../src/VamSys.Infrastructure/OperationsUnknownResolution.cs) | **判定和证据合并为一个函数。** `Matches` 决定是否成功，`RecordReadback` 另外生成差异列表，两者各自遍历期望值，jumpseat 的 null 规则重复。父机队不符、机场别名不符、jumpseat 时间窗或“新建前已存在”导致的未知不会出现在差异列表里，诊断里看不出原因。改为一个返回“是否通过＋完整原因列表”的函数，`Matches` 和证据都取自它；`MatchesCreated` 改为传入期望值，不再临时改写 `item.ApiExpectedValues`。 | 行为不变：现有核对测试全部通过；新增测试：每一种未通过原因都出现在差异列表中 | 无 |
| 3 | G3 | [OperationsBatchExecutor.cs:144-152](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs) | **回读不一致时隔几秒再读一次。** 写入后立即回读，服务器缓存或延迟可能读到旧值，直接判为未知。不一致时等待 2–5 秒再 GET 一次，仍不一致才判未知。只在出问题时多 1 次读请求，仍受共享限流约束。 | 测试：第一次旧值、第二次新值时判为成功；两次都不一致时为未知，证据保留两次回读 | **需决定**等待秒数（建议 3 秒） |
| 3 | G5 | [OperationsAdapter.cs:469-476](../src/VamSys.Infrastructure/OperationsAdapter.cs) | **jumpseat 时间窗改用服务器时钟。** 恢复 jumpseat 新建时，要求 `created_at` 落在本机记录的请求时间 ±60 秒内；本机时钟偏差超过 60 秒，真实匹配会被拒。诊断已记录服务器响应头 `Date`（`ApiDiagnostic.ServerDate`），用它估算本机与服务器的偏差后再比较；没有 `Date` 时沿用现有判定。 | 测试：本机时钟偏差 5 分钟时仍能正确匹配；偏差未知时行为不变 | 无 |
| 4 | G4 | [OperationsAdapter.cs:499-518](../src/VamSys.Infrastructure/OperationsAdapter.cs) | **把已证实的服务器规范化写成白名单。** 回读比对现在是严格相等（日期按时间值，数组按集合）。若真实证据显示服务器会规范化某些字段（去空格、改大写、数字与字符串互换），按字段登记为等价；只登记有证据的字段。 | 每条白名单规则都附真实证据编号和回归测试 | **需真实写入取证**：用独立脚本对自建记录写入并回读，完成后清理 |
| 5 | G7 | [OperationsBatchExecutor.cs:92](../src/VamSys.Infrastructure/OperationsBatchExecutor.cs) | **飞机删除的恢复可能误报成功。** 飞机只能按 `fleet/{机队}/aircraft/{ID}` 读取。删除超时、同时别人把飞机移到别的机队时，按原机队路径读到 404，会被判为删除成功并移除本地记录。判为成功前，按注册号在各机队查一遍（`filter[registration]`）。 | 测试：飞机被移走时保持未知；确实删除时仍判成功 | **需真实 GET**：确认 `filter[registration]` 的匹配方式 |
| 5 | G8 | [IndependentApiEvidence.cs:18](../src/VamSys.Core/IndependentApiEvidence.cs) | **其他类型航线的删除总要人工确认。** 软删除自动证明只覆盖 scheduled 和 jumpseat 航线；charter、cargo 等即使收到 204 也停在未知。这是证据范围问题：用独立脚本对自建记录补测这些类型后扩大覆盖。 | 新类型的证据登记到 [证据清单](VERIFIED-API-SCOPE.md) 和能力矩阵，并有回归测试 | **需真实写入取证** |

**不建议做：** 用写入响应代替回读 GET（现有计划已明确不做）；回读不一致时自动重发写入。

## 路线图

所有阶段都遵守 [代码优化计划](OPTIMIZATION-PLAN.md) 中“不能改变的要求”：写入不自动重发、回读确认、限流预算、判重与冲突检查、保存顺序、兼容性。每一步都要通过全部自动测试；行为修复先补失败测试（D1）；涉及存储时还要通过迁移测试和真实进程终止测试；性能项要在同一台机器上记录修改前后数字；涉及界面的项完成时做窗口验收。

排序原则：先解除会让工作区卡死的问题，再修数据处理错误，然后是不依赖存储改造的 API 改进，再做收益最大的存储改造，最后是体验、结构和工程。**加粗**的项需要先做决定或真实验证，未满足前跳过，不阻塞同阶段其他项。

### 阶段 1：解除卡死

目前任何一条待执行或未知项都会挡住整个工作区的刷新、提交、保存连接设置和维护清空（见 F8），所以这些出路缺失的影响等同于 P1。

| 顺序 | 内容 | 依赖 | 状态 |
|---|---|---|---|
| 1 | F1 放弃未发出的待执行项 | 无 | 已完成（2026-10-20） |
| 2 | G6 判定与证据合并（行为不变，为 G1 提供完整差异） | 无 | 已完成（2026-10-20） |
| 3 | G1 已接受但回读不一致的写入：接受服务器当前值 | 建议在 G6 之后 | 已完成（2026-10-20） |
| 4 | G2 (b) 机场／航线删除未获应答且 GET 404：人工确认 | 无 | 已完成（2026-10-20） |
| 5 | G2 (a) 机场／航线删除未获应答且 GET 200 与修改前一致：提醒修改前后数据一致，用户确认后标为未生效 | 决定 #3（已决定） | 已完成（2026-10-20） |
| 6 | A1 机场别名多义导致刷新失败 | 无 | 已完成（2026-10-20） |

**完成情况（2026-10-20）：** 各项先补能在旧逻辑上失败的测试，修复后回植旧逻辑确认会被发现；完整 271 项通过，没有真实 API 请求。实现中的取舍：

- F1 只处理在线任务；放弃的项标为失败并用说明码区分，没有新增状态。多个项在一次检查点提交中保存。
- G1 不覆盖飞机跨机队移动（PATCH 后只能按目标机队路径读取，读不到时无法取得服务器当前值），这类未知项仍只能在 Orwell 核对。GET 返回 404 或服务器值已与发送值一致时不接受，分别提示改用“继续”或到 Orwell 核对。
- G2 (b) 确认后保留“发送中”步骤作为证据；维护清空的判断改为把已变基的成功或人工确认项视为已结束。
- G2 (a) 按决定 #3 实现为“提醒＋人工确认”，不是自动证明：只读 GET 返回 200 且按修改前比对一致时，对话框提醒修改前后数据一致，用户确认后才标为失败（说明码 `ApiSoftDeleteUnchanged`，证据来源 `SoftDeleteUnchanged`），删除草稿保留；不一致时列出差异，状态不变。与“证明未生效”一样，标记后把“发送中”步骤改回“已准备”。
- A1 没有在冲突列表中提示多义别名：未匹配的本地行保留为新行，提交时由执行器的判重拦截重复新建。
- 新按钮和对话框（F1、G1、G2 (b)）尚未做窗口验收（见 D4）。

### 阶段 2：数据处理修复与工程底线

| 顺序 | 内容 | 依赖 |
|---|---|---|
| 1 | D2 持续集成（在后续较大改动前建立） | 无 |
| 2 | A2 统一 `local:` 解析 | 无 |
| 3 | **A3** 统一停用日期 | 决定 #1 |
| 4 | A4 批量平移的输入校验与逐行跳过 | 无 |
| 5 | A7 清理无用代码与易误读写法；C5 统一数据目录 | 无 |
| 6 | D3 脚本编码与 `test-localization-ui.ps1`（后续窗口验收要用） | 无 |
| 7 | F7 文档和完成提示：不必为确认结果而刷新 | 无 |

### 阶段 3：API 与核对改进（不依赖存储改造）

阶段开始时先用独立脚本完成全部[只读验证](#需要真实-get-验证的事项)，结果决定本阶段后半部分和后续若干项是否进行。

| 顺序 | 内容 | 依赖 |
|---|---|---|
| 1 | E1＋**E3**＋G5 新建恢复与时间窗口（一组） | E3 需决定 #4；E3 未决定时先做 E1、G5 |
| 2 | E2 每轮结束时只读核对新产生的未知项 | 无 |
| 3 | **G3** 回读不一致时隔几秒再读一次 | 决定 #5 |
| 4 | **E4** 刷新加大每页条数 | 只读验证 1 |
| 5 | **E5** 按资源刷新 | E4 完成 |
| 6 | **E6** 刷新失败后只重读失败的资源 | 决定 #6 |

### 阶段 4：增量持久化（收益最大）

| 顺序 | 内容 | 完成标准 |
|---|---|---|
| 1 | **C1** 在 `ResourceData` 中实现变更日志，存储按日志增量写入 | 决定 #2；新测试覆盖替换、追加、删除、重排、列和冲突变化，以及保存失败后回退到完整保存；随机操作与完整保存结果逐字节对照 |
| 2 | B1 批量操作和新增行改为增量保存 | 5 万行时批量编辑 200 行、新增一行的保存耗时从约 2 s 降到 100 ms 以内（目标值，以实测为准） |
| 3 | B2 冲突处理只在结束时保存 | 解决 100 个冲突只保存 1 次 |
| 4 | B3、B5 任务、连接和设置只写头部或任务表 | 提交、重试、连接、保存设置不再重写行表 |
| 5 | B4 定向查询任务 | 提交后的确认和诊断导出不再完整读取工作区 |
| 6 | 原计划第 1 项：切换工作区和关闭窗口只写变更日志 | 5 万行离开工作区从约 2.4 s 降低，并记录每轮原生内存增长 |
| 7 | 原计划第 4 项：剩余的完整保存放到后台序列化 | 界面线程上不再有完整序列化 |
| 8 | F6 提交前未修改时跳过重建计划（复用变更日志） | 未修改时提交不再重建；任何修改入口都会使其重建 |

### 阶段 5：界面线程、读取与提交体验

| 顺序 | 内容 | 完成标准 |
|---|---|---|
| 1 | F3 确认对话框汇总并显示预计耗时 | 见 F3 |
| 2 | F2 执行期间允许只读浏览 | 见 F2 |
| 3 | F4 样本通过后直接提交剩余项 | 见 F4 |
| 4 | 原计划第 3 项：后台读取工作区，行 JSON 使用源生成，复用字段名字符串 | 5 万行启动到首帧和读取耗时、分配量有前后对比 |
| 5 | B6 在线检查移到后台 | 界面最长停顿有前后对比 |
| 6 | 原计划第 2 项：审阅计划只保存变化字段 | 5 万项变更的分配量和界面停顿有前后对比 |
| 7 | 原计划第 5 项、B8、B9：缓存快照索引和解析结果，减少校验分配 | 核心基准（`--benchmark-core`）有前后对比 |
| 8 | B7 执行器去掉二次方查找 | 1 万条演示任务的总耗时有前后对比 |

### 阶段 6：结构整理（行为不变）

| 顺序 | 内容 | 完成标准 |
|---|---|---|
| 1 | C6 `.editorconfig` 与分析器（先于其余整理，便于约束新代码） | 新增警告清零；只格式化本次修改的文件 |
| 2 | C2 抽出 `WorkspaceSession`、`DemoRunner` | 每次只移动一块，测试和窗口脚本通过 |
| 3 | C4 字段规格表；拆分 `OperationsAdapter.Plan` 和执行器主循环 | 请求体逐字节不变 |
| 4 | C3 演示与在线执行器共享检查点（B3 中的演示执行器改造在此完成） | 演示任务的中断恢复与在线一致 |
| 5 | A5、A6、B10 | 约束写入文档并加断言；日志轮换；连接寿命 |

### 阶段 7：取证、验收与收尾

| 顺序 | 内容 | 依赖 |
|---|---|---|
| 1 | **G4** 服务器规范化白名单 | 真实写入取证 1 |
| 2 | **G8** 扩大软删除证明的航线类型 | 真实写入取证 2 |
| 3 | **G7** 飞机删除恢复前按注册号确认 | 只读验证 4 |
| 4 | **E8** 新建机场／机队按数量选择判重方式 | 只读验证 2 |
| 5 | **E10** OAuth 最小权限 | 只读验证 3 |
| 6 | D4 现有界面窗口验收、高对比度检查 | D3 |
| 7 | 文档：更新 [测试报告](TEST-REPORT.md)、[性能报告](PERFORMANCE.md)、[更新日志](../CHANGELOG.md)、[README](../README.md)，把已完成部分并入 [代码优化计划](OPTIMIZATION-PLAN.md) | 无 |

### 待决定后排期

| 内容 | 决定 | 建议放入 |
|---|---|---|
| E7 429 和“请求未发出”的写入本轮内再发一次 | #7 | 阶段 3 |
| E9 新建机队不再每条重读批准参数 | #8 | 阶段 3 |
| F5 在线模式审阅时运行本地校验 | #9 | 阶段 5 |
| F8 缩小全局阻止范围 | #10 | 阶段 5（在阶段 1 的出路都完成之后，影响会小很多，可以重新评估是否仍然需要） |
| F9 允许只提交一部分 | #11 | 阶段 5 |

## 需要决定的事项

按影响的阶段排列：

1. **A3**（阶段 2）：停用航线的结束日期应为当前 UTC −1 天还是 −2 天。**2026-11-04 决定：−1 天。**
2. **C1 的范围**（阶段 4）：变更日志是否同时覆盖撤销历史（目前已有独立的增量保存），还是只覆盖行和元数据。建议先只覆盖行和元数据，撤销历史沿用现有机制。
3. ~~**G2 (a)**（阶段 1）：机场或航线删除未获应答、GET 返回 200 且与修改前一致时，是否允许“证明未生效”。这会改变“软删除不能用读取证明”的现有规则。~~ 已决定（2026-10-20）：提醒修改前后数据一致，由用户确认后标为未生效，不自动判定。
4. **E3**（阶段 3）：写入超时取多少秒（建议 60–90 秒）。
5. **G3**（阶段 3）：回读不一致后等待几秒再读（建议 3 秒）。
6. **E6**（阶段 3）：刷新失败后保留的已读结果最长可用多久（建议仅限同一会话、几分钟内）。
7. **E7**（阶段 3）：是否在规则中为 429 和“请求未发出”的写入开一个“本轮最多再发一次”的例外。
8. **E9**（阶段 3）：同一轮内是否接受轮次开始时读取的批准状态。
9. **F5**（阶段 5）：在线模式下本地校验问题只提示，还是阻止提交（建议只提示，相关条目标为受阻）。
10. **F8**（阶段 5）：是否把冲突和未知项的阻止范围缩小到相关记录和依赖项。
11. **F9**（阶段 5）：是否允许只提交一部分，入口放在审阅页还是数据页。

## 需要真实 GET 验证的事项

只读请求，不做写入。建议用独立脚本一次完成并归档结果：

1. **E4**：`page[size]=500` 是否按 500 返回、是否有隐藏上限、游标翻页是否连续（对比 100 条一页的全部 ID 集合）。
2. **E8**：`filter[icao]`、`filter[iata]`、`filter[name]` 的大小写与匹配方式。
3. **E10**：可用的 OAuth scope（只取令牌和 GET）。
4. **G7**：飞机列表 `filter[registration]` 的大小写与匹配方式。

## 需要真实写入取证的事项

只针对当轮自建记录，完成后清理，结果登记到 [证据清单](VERIFIED-API-SCOPE.md)：

1. **G4**：哪些字段会被服务器规范化，以及规范化规则。
2. **G8**：charter、cargo 等其他类型航线软删除后的 204／404 行为。
