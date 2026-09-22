# FlightOps Desk

<img src="src/VamSys.App/Assets/FlightOpsDesk.png" width="96" height="96" alt="FlightOps Desk 图标">

面向 vaMSYS 的 Windows 运控数据管理工具，通过 Operations API 批量管理机场、机型、飞机、航路和航线。

**连接 VA → 刷新数据 → 编辑／批量修改 → 审阅变更 → 提交 API → 回读核对**

C# · .NET 10 · WinUI 3 · Windows 11 x64 · 简体中文 / English

## 当前状态

已实现 API 请求管线、本地缓存、草稿恢复、任务检查点及冲突处理。**尚无真实实例联调结果**；本地契约与模拟测试不代表真实服务器验证。契约不明确的能力保持禁用，详见 [API 契约与验证状态](docs/API-CONTRACTS.md)。

软件名称统一为 FlightOps Desk，仓库地址继续使用 vamsys-bunch。当前工具管理运控数据，不提供油量计算或天气简报等完整签派功能。

## 主要功能

- 多 VA 独立工作区，凭据使用 Windows 当前用户 DPAPI 保护。
- 数据搜索、多选、编辑与批量规则，机型关联预览和删除审阅。
- CSV 导入候选变更、差异导出及未知列保留。
- 三方冲突比较；未知写入只核对，不自动重发。
- SQLite v3 资源行原子重基线、任务增量保存及进程恢复。
- 虚拟化任务列表与局部更新；中英文即时切换。

## 快速开始

1. 获取 Windows x64 自包含目录包，解压到独立目录。
2. 关闭旧版，运行 FlightOpsDesk.exe，并保留同目录的全部文件。
3. 创建 VA 工作区，在“连接与设置”保存 Operations Client ID / Secret。
4. 测试连接后手动刷新数据；编辑完成后，在审阅页明确提交。

仓库源码 ZIP 不是可运行程序包；需要自行构建时见下文。没有实例凭据时，可使用明确标识的演示工作区和离线 CSV 流程。

[完整使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md) · [文档索引](docs/README.md) · [更新日志](CHANGELOG.md)

## 从源码构建

在 Windows 上准备 .NET SDK 10.0.401 和 Windows SDK／WinUI 构建环境。SDK 与 NuGet 依赖已锁定；本地 .tools/dotnet SDK 存在时优先使用，否则调用 PATH 中的 dotnet。

使用 PowerShell 7：

~~~powershell
./scripts/build.ps1 -Publish
~~~

该命令先运行行为回归，再发布自包含程序到 artifacts/flightops-app。运行该目录中的 FlightOpsDesk.exe。

仅运行行为测试：

~~~powershell
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
~~~

图标源文件及重新生成方法见 [品牌说明](docs/BRANDING.md)。

## 仓库结构

| 目录 | 内容 |
|---|---|
| [src/VamSys.App](src/VamSys.App/README.md) | WinUI 3 界面、视图模型及品牌资源 |
| [src/VamSys.Core](src/VamSys.Core/README.md) | 业务规则、变更规划、消息和任务模型 |
| [src/VamSys.Infrastructure](src/VamSys.Infrastructure/README.md) | Operations API、判重索引与 SQLite 存储 |
| [tests/VamSys.Tests](tests/VamSys.Tests/README.md) | 行为、契约、恢复及性能测试 |
| scripts | 构建、图标生成与实际窗口验证脚本 |
| docs | 使用、契约、迁移、测试和性能文档 |
| examples | CSV 示例 |

构建产物、测试数据库及本机 SDK 保留在忽略目录中。历史内部项目名和数据目录继续保留，以兼容已有工作区。

## 验证记录

第八轮完整业务回归为 **151 项通过**。之后品牌更新完成自包含构建及实际双语窗口验证，没有重复运行全部业务回归。测试范围、历史记录和实例验证边界见 [测试报告](docs/TEST-REPORT.md)；基准方法及本机测量见 [性能报告](docs/PERFORMANCE.md)。
