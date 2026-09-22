# FlightOps Desk 文档索引

## 使用与升级

| 文档 | 内容 |
|---|---|
| [使用指南](USER-GUIDE.md) | 连接、编辑、提交、删除、恢复及本地数据 |
| [存储迁移与恢复](STORAGE-MIGRATION.md) | v3 数据结构、迁移备份和任务恢复边界 |
| [品牌说明](BRANDING.md) | 软件名称、图标源文件和旧版兼容 |

## 开发与验证

| 文档 | 内容 |
|---|---|
| [API 契约](API-CONTRACTS.md) | 官方来源、资源转换、验证状态及禁用能力 |
| [测试报告](TEST-REPORT.md) | 自动回归、实际窗口验证和历史审核记录 |
| [性能报告](PERFORMANCE.md) | 索引、存储和任务界面的测量与复现 |
| [多语言维护](LOCALIZATION.md) | 资源键、结构化消息及即时切换规则 |
| [更新日志](../CHANGELOG.md) | 按日期归纳主要改动 |

官方 OpenAPI 快照与获取信息保存在 contracts 目录。完整回归、模拟服务验证、实际窗口验证和真实 API 实例联调是不同验证层级，请结合各报告中的日期与边界阅读。

## 各项目说明

- [App：桌面界面](../src/VamSys.App/README.md)：页面分工、任务局部更新、关闭保护、窗口构建。
- [Core：业务核心](../src/VamSys.Core/README.md)：领域模型、变更规划、CSV、分配删除和消息边界。
- [Infrastructure：外部适配](../src/VamSys.Infrastructure/README.md)：API、判重、未知恢复、SQLite 与迁移事务。
- [Tests：验证项目](../tests/VamSys.Tests/README.md)：执行命令、场景分工、子进程恢复和窗口验证边界。
