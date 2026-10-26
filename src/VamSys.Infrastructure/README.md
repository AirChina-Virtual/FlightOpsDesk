# VamSys.Infrastructure — 连接服务器

这个项目负责和 vaMSYS 通信。它会检查重复记录，在请求超时或程序中断后判断服务器是否已经完成操作。项目使用 net10.0，引用 Domain、Application 和 Storage。`AppPaths` 在这里解析环境变量与默认数据目录；`LocalizationDiagnostics` 订阅领域的缺键事件并写入 Trace，宿主启动时启用一次。

程序使用的 Operations API 文档副本放在 docs/contracts/operations.json，编译时会以 Operations.OpenApi.json 的名称打包进去。Pilot API 文档仅供参考，目前没有接入。

## 哪些文件负责连接和提交

| 文件 | 具体作用 |
|---|---|
| Transport/OperationsTransport.cs | 传输门面：保留原有的公开接口，转交给下面各部分 |
| Transport/RequestPipeline.cs | 一次请求的完整流程；读与写是两个入口，只有读入口带重试策略 |
| Transport/RequestBudget.cs | 同一 VA 的请求预算：一次一个、间隔 ≥1 秒、Retry-After、剩余额度为 0 时等一分钟（`RequestCoordinator` 持有各 VA 的预算和时钟） |
| Transport/TokenProvider.cs | OAuth 取令牌，并按 `expires_in` 缓存 |
| Transport/ResponseClassifier.cs | 把 HTTP 结果或异常归为未发出／被拒未处理／已接受／结果未知／字段错误／其它 |
| Transport/ReadRetryPolicy.cs | 只用于 GET 的退避重试 |
| Transport/Pager.cs、OriginPolicy.cs | 游标分页（页长 100，被拒时退回默认）；HTTPS、同源和路径限制 |
| Transport/TransportExceptions.cs | `ApiResponseException`、`RequestNotSentException` |
| Transport/ApiDiagnostics.cs | 结构化错误、脱敏、正文限制和诊断导出；OAuth 正文不进入证据 |
| OperationsParameters.cs | 参数只读查询、会话缓存、批准前缀预检与字段差异证据 |
| OperationsAdapter.cs | 只剩外观：把调用转给 Api/ 下的组件（ResourceMapper、RequestPlanner、ContractValidator、StepWriter、ReadbackVerifier、DuplicateFinder、IdentityIndex、DeletionVerifier、UnknownResolver、MovementVerifier、AirlineParameters、SnapshotRefresher）；RowFacts 按行缓存 RawApiJson 中的别名、端点 ID、原始时间 |
| Api/Pipeline/ | 执行流水线：ExecutionPipeline 依次调用预检、判重、写入、回读、恢复、失败处理和检查点各段；条目字典 ItemIndex 与快照按位置删除 SnapshotPositions |
| OperationsDeletionVerification.cs | 软删除 204／404 证明、纯 GET 历史核对与提交后生效的人工确认；不执行 Pending 写入 |
| CandidateIndex.cs | 按 ID、名称或注册号等信息快速查找记录；同名的不同记录都保留 |
| OperationsBatchExecutor.cs | 只剩两个入口，转交 ExecutionPipeline |
| DemoService.cs | 提供带有演示标记的测试数据；连接真实服务器失败时不会偷偷切换到演示 |
| ApiContracts.cs | 契约版本与哈希、未验证适配器 |

有些记录需要先创建，再补充其他属性。创建返回 ID 后，必须先把 ID 和进度保存成功，才能提交下一步。如果上次停在“执行中”或“结果未知”，恢复时只查询结果，不直接重发新增或修改请求。账号或权限出错时任务暂停；其他记录失败也不能让一条结果未确认的记录失去防重复保护。

识别机场时同时检查 ICAO 和 IATA 代码；比较航线起降机场时，使用当前航空公司在服务器上的机场 ID。机场代码查找使用 OperationsAdapter 内的索引：代码、远端 ID、ICAO、IATA 都映射到远端 ID 集合，只有恰好对应 1 个机场时才能使用。索引在已读机场、机场快照列表或当前航空公司变化时自动重建。判断快照是否变化时，依据列表对象、行数和最后一行，因此修改快照时只能替换列表，或者删除、追加行，不能直接改已有行的内容。

航线和航路新建前，按远端起降机场 ID 查询候选。同一次执行中，每个机场对只查询一次：只有全部分页读完，结果才会缓存；如果第一页就发现重复，会立即停止，不缓存不完整的结果。本批确认成功的新建、修改和删除，会用确认后的版本替换缓存中的旧版本，改变了起降机场的旧版本也会移除。结果未知的写入仍由原有的预留机制保护。缓存只在一次执行内有效，恢复任务时重新读取。

完整刷新时，读取飞机会直接使用本次已读到的机型，不再重复请求机型列表。刷新数据先在新的集合中合并，保存成功后才替换当前工作区；不会深拷贝全部任务和撤销历史。用于查重的数据必须全部读取成功后才能使用，并在确认修改成功后更新。如果一个机场是否创建成功还不清楚，就先暂停后续机场创建，避免换一种机场代码又创建一次。

## 本地保存

工作区、任务进度、设置和凭据的本地保存由 [VamSys.Storage](../VamSys.Storage/README.md) 负责；本项目只通过 IBatchCheckpointWriter 等端口写入进度，不直接接触 SQLite 或 Windows 密钥保护。

## 已经测试什么，还有什么不能确定

Tests 使用模拟服务器和单独的测试数据库，检查分批读取、请求限速、冲突、超时、ID 保存失败及程序被中断后的恢复。飞机移动和软删除按独立真实请求与回读证据开放，软件模拟回归另行登记，不替代本轮真实写入或窗口验收；未验证清空、单位和复杂对象仍受限制。

[API 契约](../../docs/API-CONTRACTS.md) · [迁移说明](../../docs/STORAGE-MIGRATION.md) · [性能报告](../../docs/PERFORMANCE.md)
