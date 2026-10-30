# VamSys.Storage — 本地数据库

这个项目负责 `workspaces.db`：工作区、任务、数据行、撤销历史、设置和凭据密文的读写，以及旧格式升级。数据库文件格式就是 [存储格式 v4](../../docs/STORAGE-MIGRATION.md)，本项目的重构没有改动它的任何一个字节。项目使用 net10.0，引用 Domain 和 Application，依赖 Microsoft.Data.Sqlite 和 Windows 的密钥保护组件；Infrastructure 和界面程序通过它访问数据库，Domain、Application 不引用它。为了不改动调用方，命名空间仍是 `VamSys.Infrastructure`，程序集名为 `VamSys.Storage`。

## 文件分工

| 文件 | 具体作用 |
|---|---|
| WorkspaceStore.cs | 对外的唯一入口：公开方法与改造前完全一致，每个方法只委托给下面的某一个类 |
| SqliteConnectionFactory.cs | 连接字符串、`foreign_keys`／`synchronous=FULL` 等 PRAGMA、WAL、备份目标库 |
| StorageContext.cs | 同一数据目录共用的写锁、连接工厂和故障注入点 |
| DataDirectoryLease.cs | 防止两个程序同时操作同一个数据目录 |
| SchemaV4.cs | 全部建表、索引、触发器语句的唯一来源，按创建顺序排列 |
| Migrations/ | `MigrationRunner` 是共用框架（先备份并检查、单事务、最后写 `user_version`）；`LegacyBlobMigration`、`V2Migration`、`V3Migration` 各自只处理本格式的数据 |
| Repositories/ | `WorkspaceRepository`（头部与保存版本）、`JobRepository`（任务与条目）、`ResourceRowRepository`（字段定义、冲突、行、重基线）、`HistoryRepository`（撤销／重做步骤）、`SettingsRepository`、`SecretStore`（DPAPI） |
| RowSerializer.cs | 行、任务头、条目和历史步骤的 JSON，以及保存前一次性捕获的载荷记录；输出格式由存储金样逐字节锁定 |
| UnitOfWork.cs | 一次保存＝一个工作单元：同一事务内检查版本、写入、推进版本再提交，陈旧副本被拒绝 |
| WorkspaceWriter.cs、BatchCheckpointWriter.cs | 完整保存、变更日志保存、单元格行保存和检查点写入；先序列化，再进入工作单元，提交成功后才确认历史和日志 |
| Repositories/JournalRepository.cs | 把一次日志保存（头部、任务、行增删改与重新编号、字段定义、历史）写进调用方的事务；任何一条本应恰好改一行的语句不是一行就整体撤回 |
| SaveReport.cs | 最近一次保存实际写了什么（行、删除、移动、元数据、任务、条目、头部、JSON 字节），以及不加载工作区就能读出的各资源行数 |
| MaintenanceService.cs | 未决写入保护、整库备份与清单、仅当前工作区的事务清理 |
| IStorageFaults.cs | 存储操作中的命名故障点；正式构建里调用连同参数一起被编译器移除，测试实现在 tests/TestSupport |
| PerformanceRun.cs、OperationsCredentials.cs、StorageErrors.cs | 保存统计、凭据记录和本地化错误 |

## 保存和升级时保护什么

保存前先把 JSON 全部生成，进入事务后只做写入；失败不留下半个事务，也不先改内存。任务显示成功、更新本地数据、清理对应撤销记录等变化作为一次保存一起成功或一起失败。升级前用 SQLite 备份功能保存完整副本，所有步骤成功后才更新版本号；发现损坏数据、重复编号，或数据库版本比程序支持的更新时，停止处理，不擅自改写数据。凭据继续由当前 Windows 用户加密，升级前的备份不能用来回滚服务器上已经发生的修改。

编辑表格单元格后的自动保存使用 `TrySaveDraftRowsAsync`：只更新目标草稿行、变化步骤和必要的历史栈顺序，任何一行更新数量不等于 1 都整体撤回；内存中找不到被编辑的行时返回 false，由调用方改用完整保存。其他操作使用下面的变更日志保存。

## 变更日志（R3 第 3、4 项）

**目的。** 保存只写发生了变化的部分，日志为空就什么都不写；每次变化的保存时机不变（非单元格改动仍然立即保存），变的只是写入的字节数。

**日志记录什么。** 每个 `Workspace` 带一个 `ChangeJournal`（`VamSys.Domain/ChangeJournal.cs`），它保存“数据库上一次与内存一致时”的样子：每个资源的快照行和草稿行（行对象引用的数组）、字段定义、冲突、快照时间、工作区头部的内容键、任务对象数组。行从不原地修改——`DataRow` 构造后不可变，由编译器在 Release 下同样强制（`LocalId`／`Identity`／`RawApiJson` 只能 init，`Fields` 是只读视图，改行只能用 `With`／`WithIdentity`／`ToBuilder` 等得到副本并替换），不再依赖只在 Debug 生效的冻结守卫，Release 中原地修改导致日志漏记的风险已消除（风险说明：此前 Release 下的原地修改会被静默跳过而丢失；`Columns`、冲突、任务等非行数据仍按值比较或标记，见下）；所以待写入的变化由日志把这份记录与当前列表比较得出：同编号的新行对象＝替换（内容逐字节相同的替换不写）、新编号＝新增、缺失编号＝删除、位置变化＝重新编号。因为是比较而不是在每个修改入口登记，`Draft`／`Snapshot` 的赋值、`ReplaceDraftRows`、`Edit`、导入、合并、直接对列表 `Add`／`Remove`／`Insert`／`Reverse` 等全部路径都被覆盖，不可能漏记。字段定义、冲突、快照时间、头部按值比较，所以对 `Columns.Add` 这类原地修改同样有效。

**任务有两道保险。** 任务和条目由执行器原地修改，无法按引用比较。快速路径是显式标记：`workspace.Journal().TouchJob(job)`（`BatchCheckpointWriter` 在每次检查点开始时自动标记；演示执行器、重置失败条目、演示收尾在界面里标记），被标记的任务整体重写。安全网是状态指纹：日志为每个任务保存一个不经序列化的摘要（任务状态，以及每个条目的状态、标志位、远端编号、消息、诊断、API 步骤状态和证据对象），任务范围的保存对每个未标记的任务重新计算并与保存时的摘要比较，不同就重写——所以漏标记也不会丢任务状态（它决定能否再次发送写入）。指纹不是完整 JSON 的哈希，条目其他细节的原地修改仍应标记。选择指纹而不是序列化哈希的原因是成本：1000 个历史任务×20 条＋1 个 20000 条的活动任务，指纹比较约 6 ms（空日志保存），整体序列化一次约 100 ms。只写头部的保存不比较任务（0.7 ms）。`MarkRowsDirty(kind)` 让某资源的行在下次保存时整体重写，检查点写入的行位置与日志的假设不同，所以检查点之后自动使用它。

**写入范围。** 新增在末尾、替换、删除末尾行只写变化的行；中间删除或移动时，位置变化的行先挪到负数位置再写最终位置（保持“同表同集合内位置唯一”的索引）；变化的行数超过 `max(256, 行数/20)` 时该列表整体重写。头部变化只更新 `workspaces` 一行；任务新增写任务和条目，被标记的任务重写头部和全部条目；元数据只在字段定义、冲突或快照时间变化时更新。撤销历史不在日志里（决定 R-D5）：沿用 `EditHistory` 自己的增量机制，在同一事务里写变化的步骤和栈顺序。

**什么时候回退到完整保存。** 日志只在“记录的版本号＝工作区当前版本号”时可信。从未保存过的工作区、加载之后被别的写入者（例如 API 刷新用的暂存副本，它在提交后把自己的日志交给工作区）推进过版本号的工作区、任何一次保存失败之后，都不可信，下一次保存是完整保存。增量写入本身不合数据库（例如某行更新数量不是 1）时，本次保存立即以完整保存重做。`SaveChanges` 遇到版本陈旧照旧拒绝，不写任何东西。

**提交与失败。** 日志计划在界面线程上算出并序列化成 JSON，事务内只做写入，提交成功后才推进日志、确认历史。保存失败时数据库和内存都不变，日志不前进并要求下一次完整保存（与 `DraftEdits.Restore` 一致）。保存过程中又发生的改动保持待写。

**范围保存和定向读写。** `SaveHeader`／`SaveJobs`（或 `SaveChanges(w, scope)`）只写工作区头部／任务，其余变化留在日志里等下次。`JobExists`、`LoadJob`、`CountRows` 不加载工作区；维护预览用它们和只读任务列表，不再加载全部行。保存连接设置不再序列化再反序列化整个工作区，只和其他待写变化一起写头部与密文。

**仍然整体重写的情形。** 从未保存的工作区；位置变化的行很多（大范围重排、在长列表靠前位置删除或插入）时该列表；任务检查点触及的资源（执行期间每个检查点写一行，收尾保存时重写该资源）；变基检查点后的所有资源；被标记的任务（其全部条目）；保存失败后的下一次保存。

## 测试钩子如何隔离

`VamSysTestSupport=true` 的构建会把 tests/TestSupport 里的 `ActionStorageFaults`、`WorkspaceStore.Barrier`、供 QA 窗口（不是友元程序集）设置屏障的公开 `StorageTestHooks`，以及测试友元授权编译进来，输出放在 artifacts/test-build。正式程序集不含这些类型、属性和友元授权，`--verify-package` 会检查。测试项目必须直接引用本项目并带上同一个 `VamSysTestSupport` 属性，传递引用不会带过去。

[迁移说明](../../docs/STORAGE-MIGRATION.md) · [Infrastructure](../VamSys.Infrastructure/README.md)
