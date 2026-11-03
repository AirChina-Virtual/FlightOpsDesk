# VamSys.Domain

这个项目负责判断数据应该怎样处理。例如，哪些记录是新增、哪些字段被修改、某架飞机是否还被引用，以及删除是否应该被阻止。它使用 net10.0，不负责画窗口、连接数据库或保管 API 密钥。

## 项目边界

Domain 不引用其他产品项目。文件、网络、环境目录和日志写入由外层处理，编译产物中的依赖与宿主 API 调用由架构测试检查。CSV 和 JSON 只处理内存数据；内置中英文资源、既有 UTC 时间与 GUID 生成语义保留。缺失资源通过事件报告，由 Infrastructure 的日志订阅处理。

程序集名为 `VamSys.Domain`；本轮继续使用 `VamSys.Core` CLR 命名空间和资源基名，以保持调用方、资源键及 JSON 契约稳定。数据库仍为 v4。应用编排和 I/O 端口见 [Application](../VamSys.Application/README.md)，数据目录和外部实现见 [Infrastructure](../VamSys.Infrastructure/README.md)。

## 它怎样理解一份数据

每个工作区保存五类数据，以及读取时的原始副本（Snapshot）、编辑中的草稿（Draft）、冲突和操作历史。LocalId 是程序自己给记录分配的编号；服务器 ID 另行保存，两者不能混用。

每个字段都区分“不修改”“设为某个值”和“清空”。例如，CSV 没有 Name 列，并不意味着要清空名称；文件少了一条记录，也不能自动删除它。Workspace.Serialize/Deserialize 可以把整个工作区转换为完整数据格式，怎样分开存入数据库则由 Infrastructure 处理。

## 模块说明

| 文件／类型 | 具体作用 |
|---|---|
| DataRow.cs | 数据行：构造后不可变（`LocalId`／`Identity`／`RawApiJson` 仅 init，`Fields` 为只读视图，对象初始化器会复制传入的字典），编译器在 Debug 与 Release 下同样强制，变更日志按行对象引用识别替换所依赖的前提因此不再依赖 Debug 守卫。改行只有副本方式：`With(字段, 值)`／`With(多个字段)`／`Without`／`WithIdentity`／`WithRawApiJson`／`WithLocalId`，或 `ToBuilder()` 得到可变的 `DataRow.Builder`（`ToRow()` 只能调用一次并零拷贝移交字段字典；导入、`FromJson`、历史回放等热路径每行只建一次）。`FieldEntries()` 是不分配内存的只读结构视图，供逐行遍历的热路径使用（对 `Fields` 做 `foreach` 会为每行装箱一个枚举器）。`SameContent` 按内容比较 |
| Models.cs | 定义工作区、数据行、修改清单、任务状态，以及各部分调用时使用的接口 |
| IndependentApiEvidence.cs、ApiEvidence.cs | 固定契约下的独立实测字段范围、能力键、结构化诊断及可选删除核对证据；与工作区软件回读标记分开 |
| Schemas.cs | 列出五类数据的字段及填写要求 |
| CsvAdapter.cs | 读取和输出 CSV，处理引号、换行，并保留不认识的列 |
| ChangePlanner.cs | 比较原始数据和草稿，列出要新增、修改或删除的内容及发现的问题 |
| SnapshotMerger.cs | 同时比较原始数据、你的修改和服务器最新数据，合并不冲突的改动 |
| FleetAssignmentService.cs | 算出替换、追加或移除机型后，记录最终应该关联哪些机型 |
| DeletionService.cs | 区分删除本地新草稿与删除服务器记录，并检查是否有其他记录在使用它 |
| BatchStep.cs、ResourceRebaseDelta.cs | 已保存的请求步骤和资源变化数据，不执行保存 |
| FieldSpec.cs、LocalReference.cs | 字段规格表和本地引用的统一解析 |
| EditHistory.cs | 不可变行前后状态、结构顺序、撤销重做、旧历史转换与版本化 JSON |
| ChangeJournal.cs | 每个工作区一份“数据库现在是什么样”的记录；比较它与内存得出待写的行增删改、移动、字段定义、冲突、任务和头部变化，保存成功后才前进（用法见 [Storage 说明](../VamSys.Storage/README.md)）；不引用任何存储类型 |
| Localization.cs、Resources | 选择中英文提示、填入参数，并兼容旧版保存的提示文字 |

## 用户修改一条记录后，会发生什么

1. 读取到的原始记录存入 Snapshot，用户在 Draft 中修改，先不改服务器。
2. 批量分配机型或删除时，先算出影响范围，供用户预览。
3. ChangePlanner 列出具体修改，并记录哪些内容需要先创建、哪些字段需要清空。
4. Infrastructure 按照 API 文档把修改转换成请求。CSV 表头和 API 字段可能不同，不能直接照抄。
5. 每一步的结果保存下来。再次读取服务器并确认修改成功后，才更新本地原始副本，作为下次比较的依据。

## 修改时需要注意

修改草稿使用 `ResourceData.Edit`，明确受影响 ID；结构变化使用 `structural:true`，同数量替换行使用 `replaceRows:true`。仅改单格字段不重建 LocalId 索引。`newStep:false` 修正现有撤销／重做边界，适用于同格续写及冲突解决。绕过编辑入口直接增删／替换后，必须清理历史并使索引失效。ShallowCopy 共享当前行对象，暂存修改只能替换行；历史步骤不可变，可以共享。

同 LocalId 的内容替换优先通过 `ReplaceDraftRows`，保留行位置索引，无需设置 `replaceRows:true`；增删、重排和整体替换仍使索引失效。`FindDraftRow` 返回当前行对象，`FindDraftPosition` 返回当前位置。索引不进入保存格式。

结构回放完整检查源位置和结果顺序，在同一次遍历中构建新列表与新位置索引，全部通过后才一起替换；旧索引不再使用。行副本、规划项及历史行恢复直接初始化需要的字段，避免分配随后被覆盖的默认对象，历史字段仍按原顺序恢复。

`ChangePlanner.GetChangeKind` 用于只需要状态的计数和筛选，沿用完整规划的字段比较规则，在足以判定时返回。完整规划仅为变化字段分配对象，并保留独立的变化行副本。没有结构变化的历史步骤先验证全部补丁、准备恢复对象，再替换目标行及移动栈；结构步骤继续完整验证顺序。历史校验分别使用独立的撤销／重做验证列表，不修改共享行。

`UndoEdit`／`RedoEdit` 返回 `HistoryMove`：是否移动了步骤，以及原位替换的行 ID。界面据此只保存这些行和撤销栈顺序（`DraftEdits.Rows`），并只刷新这些行。增删或重排行的步骤返回 `null`，调用方必须完整保存。

- 切换语言不能改变处理结果；日期、数字和 CSV 的格式仍按原有规则输出。
- 新的提示使用固定代码和参数。程序应根据状态代码决定下一步，不能通过提示中有没有某个中文或英文词来判断。
- 无法识别的外部错误保留原文，旧记录里的自由文本不猜测翻译。
- 能在草稿中算出机型分配结果，不表示服务器端的对应修改已经开放。
- 本地检查没有找到引用，不代表服务器上一定没有其他记录在使用它。

## 验证

从仓库根目录运行：

~~~powershell
pwsh -File scripts/check-all.ps1
~~~

自动测试会检查 CSV、机型分配、删除、冲突合并、旧提示读取以及关闭保存。这个项目不打开窗口；真正的窗口操作由 scripts 中的测试脚本检查。

[语言维护](../../docs/LOCALIZATION.md) · [API 契约](../../docs/API-CONTRACTS.md) · [项目首页](../../README.md)
