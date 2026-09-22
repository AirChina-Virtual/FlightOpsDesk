# VamSys.Infrastructure — Operations API 与 SQLite 适配

本项目负责外部系统边界：真实 Operations API 请求、资源转换、判重与恢复，以及本地 SQLite 事务。目标框架为 net10.0，引用 Core、Microsoft.Data.Sqlite 和 Windows ProtectedData。

Operations OpenAPI 快照以 Operations.OpenApi.json 名称嵌入程序集，来源文件为 docs/contracts/operations.json。Pilot 文档只保留作参考，本项目没有接入 Pilot API 流程。

## 网络与执行模块

| 文件 | 具体作用 |
|---|---|
| OperationsTransport.cs | HTTP 请求、认证令牌、请求预算、限流等待、错误分类及取消 |
| OperationsAdapter.cs | 资源请求／响应转换、能力约束、身份比较及成功回读核对 |
| OperationsQueries.cs | 分页查询、候选查询会话与资源读取组织 |
| CandidateIndex.cs | ID 和签名索引；保留同签名多实体及不确定身份预留 |
| OperationsBatchExecutor.cs | 串行写入、分步创建、写前检查点、未知结果恢复与原子重基线 |
| DemoService.cs | 明确标识的演示服务，不作为在线失败后的替代服务 |
| Performance.cs | 请求、等待、保存与执行过程的性能统计 |

创建后即使返回 ID，也需要保存检查点后才能进入补充更新。Running／Unknown 恢复只读取核对，不自动重复 POST／PUT。认证或权限错误暂停任务；失败的独立记录不会错误地释放未知身份占用。

机场按 ICAO／IATA 别名核对身份；航线和航路按当前 VA 的远端机场 ID 比较关联。判重索引仅在完整分页成功后发布，并随确认的写入结果同步。未核对的机场创建阻止后续机场创建，避免别名绕过。

## 存储模块

| 文件 | 具体作用 |
|---|---|
| WorkspaceStore.cs | 加载工作区、任务、语言和凭据等公共存储入口 |
| WorkspaceStore.Schema.cs | v3 表结构、版本检查、SQLite 一致备份和事务迁移 |
| WorkspaceStore.Persistence.cs | 全量保存、检查点提交、保存版本比较及事务顺序 |
| WorkspaceStore.Resources.cs | 资源元数据、Snapshot／Draft 行及撤销历史读写 |
| DataDirectoryLease.cs | 按数据目录限制单实例 |
| AssemblyInfo.cs | 向测试程序集开放内部故障注入与检查入口 |

v3 将工作区配置、任务头、任务记录、资源元数据、资源行和历史分别保存。普通草稿保存仍允许写完整资源集合；成功重基线只提交目标行及必要元数据，不重新序列化无关资源。

任务成功状态、资源变更、撤销清理、验证标记与保存版本必须在同一事务提交。提交失败不能先更新内存，更不能继续后续写请求。

## 迁移与保护

旧格式或 v2 升级前使用 SQLite 备份接口生成一致备份，包括已提交 WAL 数据；迁移事务全部成功后更新版本。损坏数据或重复稳定标识不静默修复，更高版本拒绝打开。

保留 Windows 当前用户 DPAPI 凭据保护。迁移备份只代表迁移时点；如果已经发生远端写入，不能直接恢复旧备份继续任务。

## 验证与能力边界

Tests 使用可控 HTTP 处理器及隔离 SQLite 验证分页、限流、冲突、超时、返回 ID 保存、事务故障和真实子进程中断。测试不证明真实 API 实例可用；未确认的机型移动、关联清空等能力继续禁用。

[API 契约](../../docs/API-CONTRACTS.md) · [迁移说明](../../docs/STORAGE-MIGRATION.md) · [性能报告](../../docs/PERFORMANCE.md)
