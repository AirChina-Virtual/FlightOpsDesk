# VamSys.Application

当前项目承接 R2 从领域层移出的 I/O 端口与应用编排，仅引用 [Domain](../VamSys.Domain/README.md)。它不依赖 SQLite、WinUI 或 Infrastructure 的实现。类型暂时保留 `VamSys.Core` 命名空间，程序集为 `VamSys.Application`。

| 文件 | 职责 |
|---|---|
| ResourcePorts.cs | 资源读取、写入和批次执行端口 |
| BatchExecutor.cs | 通用／演示执行流程、保存与进度通知顺序、异常分类 |
| BatchCheckpoint.cs | 检查点写入端口、保存请求和进度通知 |
| DraftEdits.cs | 待保存的行和历史差异、保存期间的新编辑、失败后的完整保存回退 |
| WindowCloseController.cs | 关闭时冻结、等待保存、失败后恢复操作 |

R4 增加：WorkspaceSession（保存锁、待保存编辑、自动保存、切换工作区）、InteractionGate（窗口当前能做什么的单一状态）、DiagnosticsService、MaintenanceService、ImportExportService、BatchEditService、ConnectionService、ConflictService、ReviewService（含 ReviewPlanBuilder）、SubmissionService（含 TaskSubmission）、TaskRecoveryService（含 UnknownReview）、DemoRunner。存储、在线连接和演示引擎经 WorkspacePorts.cs、OnlinePorts.cs 的端口，由 App 中的适配器提供；MainWindow 只负责对话框、控件和状态文字。

请求前后的保存顺序、未知项只读核对、已有 ID 不重复创建、保存失败不提前显示成功等规则保持不变。架构测试检查实际程序集归属和依赖方向；旧执行、恢复及关闭测试继续验证行为。
