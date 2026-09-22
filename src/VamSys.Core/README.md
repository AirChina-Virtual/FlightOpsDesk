# VamSys.Core — 独立业务核心

本项目定义 FlightOps Desk 的工作区、资源、编辑意图、变更规划和任务契约。目标框架为 net10.0，不引用 WinUI 或 SQLite，也不持有在线客户端凭据。

## 业务模型与边界

工作区包含五类资源、快照、草稿、冲突、撤销／重做和任务历史。LocalId 表示本地稳定标识，远端标识另存；不能把尚未解析的本地引用直接当作真实 API ID。

字段意图区分“未提供、设置、清空”。缺列不等于清空，源文件缺少一行也不等于删除。Workspace.Serialize/Deserialize 保留完整业务格式，数据库拆分由 Infrastructure 负责。

## 模块说明

| 文件／类型 | 具体作用 |
|---|---|
| Models.cs | 工作区、资源行、字段意图、变更集、任务状态和公开业务接口 |
| Schemas.cs | 五类资源字段及资源级约束描述 |
| CsvAdapter.cs | CSV 解析、输出、特殊字符及未知列往返保留 |
| ChangePlanner.cs | 从快照和草稿规划新增、修改、删除及校验结果 |
| SnapshotMerger.cs | 原始快照、本地草稿与最新远端数据的三方合并 |
| FleetAssignmentService.cs | 机型关联替换、追加、移除的本地目标集合计算 |
| DeletionService.cs | 新增草稿移除、已有记录待删除和当前工作区引用保护 |
| BatchExecutor.cs | 通用／演示任务执行流程及兼容调用入口 |
| BatchCheckpoint.cs | 检查点提交契约、ResourceRebaseDelta、行动作和已提交进度通知 |
| WindowCloseController.cs | 不依赖 WinUI 的关闭状态机及异步保存保护 |
| Localization.cs、Resources | 双语查找、格式化、结构化消息和历史提示兼容 |

## 一条编辑如何流转

1. 读取的原始记录进入 Snapshot，用户修改 Draft。
2. 批量分配和删除服务先生成可预览的本地变化。
3. ChangePlanner 计算需要执行的变更，保留字段意图和依赖信息。
4. 外部适配器根据真实资源契约转换请求；核心 CSV 列名不能作为 JSON 字段名直接发送。
5. 执行结果由检查点接口持久化；成功回读后，目标行才获得新基线。

## 维护时应保持的约束

- 核心逻辑不能依赖当前界面语言；日期、数值与 CSV 输出使用既有契约。
- 新业务提示使用稳定消息代码和参数，不根据中英文错误字符串判断任务流程。
- 未知外部诊断保留原文；不能推测翻译历史自由文本。
- 草稿机型集合的计算成功不代表对应在线关联更新能力已经启用。
- 删除引用检查是本地保护，不证明远端没有其他依赖。

## 验证

从仓库根目录运行：

~~~powershell
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
~~~

行为测试覆盖 CSV、关联分配、删除、三方合并、消息兼容与关闭状态机。核心层本身不打开窗口；完整窗口验证由 scripts 中的交互脚本完成。

[语言维护](../../docs/LOCALIZATION.md) · [API 契约](../../docs/API-CONTRACTS.md) · [项目首页](../../README.md)
