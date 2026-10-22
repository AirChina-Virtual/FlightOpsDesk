# VamSys.Application

当前项目承接 R2 从领域层移出的 I/O 端口与应用编排，仅引用 [Domain](../VamSys.Domain/README.md)。它不依赖 SQLite、WinUI 或 Infrastructure 的实现。类型暂时保留 `VamSys.Core` 命名空间，程序集为 `VamSys.Application`。

| 文件 | 职责 |
|---|---|
| ResourcePorts.cs | 资源读取、写入和批次执行端口 |
| BatchExecutor.cs | 通用／演示执行流程、保存与进度通知顺序、异常分类 |
| BatchCheckpoint.cs | 检查点写入端口、保存请求和进度通知 |
| DraftEdits.cs | 待保存的行和历史差异、保存期间的新编辑、失败后的完整保存回退 |
| WindowCloseController.cs | 关闭时冻结、等待保存、失败后恢复操作 |

R2 只建立这部分边界。`WorkspaceSession`、`InteractionGate`、用例服务和 `DemoRunner` 的后续拆分属于 [重构计划 R4](../../docs/REFACTOR-PLAN-2026-10-20.md#r4-应用层)，尚未完成。MainWindow 仍含既有业务编排，存储和在线 API 仍位于 Infrastructure。

请求前后的保存顺序、未知项只读核对、已有 ID 不重复创建、保存失败不提前显示成功等规则保持不变。架构测试检查实际程序集归属和依赖方向；旧执行、恢复及关闭测试继续验证行为。
