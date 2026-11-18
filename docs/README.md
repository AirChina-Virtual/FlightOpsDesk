# 文档导航

## 使用程序

| 文档 | 内容 |
|---|---|
| [使用指南](USER-GUIDE.md) | 连接、读取、编辑、审阅、提交、任务核对、本地数据；随正式包附带 |
| [升级与恢复](STORAGE-MIGRATION.md) | 旧数据怎样升级、备份怎样生成，以及为什么不能随意换回旧备份；随正式包附带 |
| [名称和图标](BRANDING.md) | 软件名称、图标文件，以及旧工作区是否还能用 |
| [更新日志](../CHANGELOG.md) | 各次更新的主要变化 |

## 参考资料（随代码更新）

| 文档 | 内容 |
|---|---|
| [架构速览](ARCHITECTURE.md) | 项目分层、依赖方向、文件放在哪里、改动时容易踩的坑 |
| [API 支持情况](API-CONTRACTS.md) | 依据哪份官方契约、支持哪些操作、哪些仍禁用 |
| [能力验证矩阵](CAPABILITY-MATRIX.md) | 每项能力的契约、独立真实 API、模拟回归和软件真实写入证据，以及各次真实验收的记录 |
| [已验证 API 开放范围](VERIFIED-API-SCOPE.md) | 解除首次样本限制的字段和类型、服务器改写的等价规则、软删除核对 |
| [代码优化要求](OPTIMIZATION-PLAN.md) | 任何优化都不能违反的规则、明确不做的优化、已完成的优化和后续方向（阶段 7） |
| [中英文维护](LOCALIZATION.md) | 怎样添加提示文字，切换语言时要注意什么 |

## 测试与性能

| 文档 | 内容 |
|---|---|
| [测试报告](TEST-REPORT.md) | 各轮检查了什么、结果如何、还有什么没验证 |
| [需求覆盖矩阵](TEST-COVERAGE.md) | 每条需求由哪些测试覆盖（由清单测试核对） |
| [回归清单](REGRESSION-LEDGER.md) | 每个已修复的问题对应的测试，以及回植检查 |
| [速度与资源占用](PERFORMANCE.md) | 大数据量下的耗时、滚动帧、内存，测量和自动比对的方法 |

“本地测试通过”“窗口测试通过”“真实账号测试通过”是不同层次的结果：模拟服务器上通过，不代表已经在真实航空公司环境中验证。`contracts` 目录保存官方 API 契约的固定副本。

## 修改代码

各项目的 README 说明文件分工、处理步骤和运行方法：

- [Domain：业务规则](../src/VamSys.Domain/README.md)
- [Application：会话、用例与端口](../src/VamSys.Application/README.md)
- [Storage：本地数据库](../src/VamSys.Storage/README.md)
- [Infrastructure：服务器通信](../src/VamSys.Infrastructure/README.md)
- [App：程序界面](../src/VamSys.App/README.md)
- [测试说明](../tests/README.md)、[窗口测试](../tests/VamSys.UiTests/README.md)、[QaSeed 工具](../tools/VamSys.QaSeed/README.md)

## 历史记录

[history](history/) 保存已完成的计划和旧报告，原文保留，只修正了链接：

| 文档 | 内容 |
|---|---|
| [彻底重构计划](history/REFACTOR-PLAN-2026-10-20.md) | R0–R8 的目标、不变规则、阶段进度和验收（已完成） |
| [审查与优化问题清单](history/REVIEW-AND-PLAN-2026-10-20.md) | A–G 问题编号与修复（已完成） |
| [综合优化计划](history/IMPROVEMENT-PLAN-2026-11-08.md) | 阶段 0–7、决定事项 D1–D11；阶段 0–6 已完成，阶段 7 待测量 |
| [数据表重写记录](history/R6-DATAGRID.md) | R6 新旧表格控件的对比与切换 |
| [软件真实流程验收清单](history/ACCEPTANCE-4.2.md) | 4.2 验收的操作步骤（结果见能力验证矩阵） |
| 其余文件 | 2026-10-19 以前的测试报告、性能报告、优化计划，以及 R2 的性能对照 CSV |

`agent-briefs` 是重构期间给子代理的任务说明，作为记录保留，其中的委派规则已不再适用。报告中提到的 artifacts 路径，原始文件可能已压缩到本机 archive 目录，见 [本地产物与归档](../README.md#本地产物与归档)。
