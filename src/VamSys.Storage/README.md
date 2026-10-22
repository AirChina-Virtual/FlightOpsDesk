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
| WorkspaceWriter.cs、BatchCheckpointWriter.cs | 完整保存、单元格行保存和检查点写入；先序列化，再进入工作单元，提交成功后才确认历史 |
| MaintenanceService.cs | 未决写入保护、整库备份与清单、仅当前工作区的事务清理 |
| IStorageFaults.cs | 存储操作中的命名故障点；正式构建里调用连同参数一起被编译器移除，测试实现在 tests/TestSupport |
| PerformanceRun.cs、OperationsCredentials.cs、StorageErrors.cs | 保存统计、凭据记录和本地化错误 |

## 保存和升级时保护什么

保存前先把 JSON 全部生成，进入事务后只做写入；失败不留下半个事务，也不先改内存。任务显示成功、更新本地数据、清理对应撤销记录等变化作为一次保存一起成功或一起失败。升级前用 SQLite 备份功能保存完整副本，所有步骤成功后才更新版本号；发现损坏数据、重复编号，或数据库版本比程序支持的更新时，停止处理，不擅自改写数据。凭据继续由当前 Windows 用户加密，升级前的备份不能用来回滚服务器上已经发生的修改。

编辑表格单元格后的自动保存使用 `TrySaveDraftRowsAsync`：只更新目标草稿行、变化步骤和必要的历史栈顺序，任何一行更新数量不等于 1 都整体撤回；内存中找不到被编辑的行时返回 false，由调用方改用完整保存。其他操作仍使用完整保存。

## 测试钩子如何隔离

`VamSysTestSupport=true` 的构建会把 tests/TestSupport 里的 `ActionStorageFaults`、`WorkspaceStore.Barrier` 和测试友元授权编译进来，输出放在 artifacts/test-build。正式程序集不含这些类型、属性和友元授权，`--verify-package` 会检查。测试项目必须直接引用本项目并带上同一个 `VamSysTestSupport` 属性，传递引用不会带过去。

[迁移说明](../../docs/STORAGE-MIGRATION.md) · [Infrastructure](../VamSys.Infrastructure/README.md)
