# FlightOps Desk

<img src="src/VamSys.App/Assets/FlightOpsDesk.png" width="96" height="96" alt="FlightOps Desk 图标">

FlightOps Desk 是一款 Windows 桌面工具，帮助虚拟航空公司在 vaMSYS 中一次处理多条机场、机型、飞机、航路和航线数据。它通过 vaMSYS 的 Operations API 读取和提交数据，减少逐条手动修改的工作。

**连接航空公司 → 读取最新数据 → 修改内容 → 检查修改清单 → 提交 → 再次读取，确认结果**

C# · .NET 10 · WinUI 3 · Windows 11 x64 · 简体中文 / English

## 当前状态

程序已经可以保存本地数据和草稿、记录任务进度，并处理本地修改与服务器数据不一致的情况。**目前还没有使用真实航空公司账号完成测试**。现有测试使用本地数据和模拟服务器，因此不能据此保证所有操作都能在真实 vaMSYS 环境中正常完成。官方文档尚未说明清楚的操作仍然禁用，详见 [API 支持情况](docs/API-CONTRACTS.md)。

软件名称为 FlightOps Desk，项目现已同步到 AirChina-Virtual/FlightOpsDesk。它主要管理运控数据，目前不提供油量计算、天气简报等完整签派功能。

## 主要功能

- 分别管理多家虚拟航空公司（VA），每家公司的数据放在独立工作区；连接密钥由 Windows 当前用户的加密机制保护。
- 搜索和筛选数据，一次选择多条记录进行修改；分配机型或删除前可以先查看影响范围。
- 导入 CSV 后先检查准备修改的内容；导出时只输出变化的记录，并保留程序不认识的列。
- 如果服务器数据也被修改，程序会比较原始数据、你的修改和服务器最新数据；有冲突时请你确认。
- 持续保存任务进度。请求超时或程序中断后，先检查服务器是否已完成操作，避免重复创建或重复修改。
- 任务较多时只显示当前可见的内容，并只更新变化的记录；中英文界面可以即时切换。

## 快速开始

1. 准备 Windows 11 x64 程序压缩包，解压到一个单独的文件夹。程序包自带所需的运行库。
2. 为你的虚拟航空公司创建工作区，在“连接与设置”填写 vaMSYS 提供的 Operations Client ID 和 Secret。
3. 测试连接并点击刷新，读取服务器数据。修改完成后，先在审阅页检查，再提交到服务器。

GitHub 的“Download ZIP”下载的是源代码，不能直接双击运行；自己编译的方法见下文。没有 API 账号时，可以使用演示工作区，或离线导入和导出 CSV。演示操作不会修改真实航空公司的数据。

[完整使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md) · [文档索引](docs/README.md) · [更新日志](CHANGELOG.md)

## 开发者：从源代码生成程序

在 Windows 上准备 .NET SDK 10.0.401 和 Windows SDK／WinUI 构建环境。项目已经指定开发工具和依赖库的版本。脚本优先使用 .tools/dotnet 中的 SDK；没有时，使用系统已安装的 dotnet。

使用 PowerShell 7：

~~~powershell
./scripts/build.ps1 -Publish
~~~

该命令先运行自动测试，通过后把可运行的程序放到 artifacts/flightops-app。运行该目录中的 FlightOpsDesk.exe。

测试工具与正式程序分开构建。正式包只包含运行文件、使用及升级说明和 CSV 示例；不包含 QA 控制入口、故障注入、测试工具、开发文档或 PDB 调试符号。测试／QA 使用的业务程序集放在 artifacts/test-build，不覆盖正式构建；测试运行器保留自己的 tests/VamSys.Tests/bin 目录。运行时需要的 Operations 规范仍嵌入程序。

生成支持窗口故障检查的专用版本：

~~~powershell
./scripts/build.ps1 -Publish -UiVerification
~~~

该版本输出到 artifacts/v3-qa，仅用于隔离测试。两种发布都会检查程序集中的测试入口和包内文件清单，验证通过后才替换各自输出目录。

只运行自动测试，不生成程序包：

~~~powershell
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
~~~

图标源文件及重新生成方法见 [品牌说明](docs/BRANDING.md)。

## 仓库结构

| 目录 | 内容 |
|---|---|
| [src/VamSys.App](src/VamSys.App/README.md) | 程序窗口、按钮、表格、多语言和图标 |
| [src/VamSys.Core](src/VamSys.Core/README.md) | 决定数据怎样修改、怎样检查以及怎样生成修改清单 |
| [src/VamSys.Infrastructure](src/VamSys.Infrastructure/README.md) | 连接 vaMSYS、查找重复记录，并在本机保存数据 |
| [tests/VamSys.Tests](tests/VamSys.Tests/README.md) | 检查功能、API 请求、中断恢复和处理速度 |
| scripts | 生成程序和图标、自动操作窗口的辅助脚本 |
| docs | 使用方法、API 支持情况、升级说明和测试结果 |
| examples | CSV 示例 |
| packaging | 正式版和 QA 版随包附带的说明 |
| artifacts | 当前正式包、QA 包、回归基线和最新验证结果（本地生成） |
| archive | 已归档的历史产物、原路径清单和校验记录（仅保存在本机） |
| .tools | 构建脚本使用的本机 .NET SDK |

生成的程序、测试数据库和本机开发工具不会提交到 GitHub。代码中的旧项目名和本地数据目录继续保留，已有工作区可以接着使用。

## 本地产物与归档

2026-09-29 整理后，当前正式程序保留在 artifacts/flightops-app，QA 版保留在 artifacts/v3-qa。artifacts/step5-v3-baseline 是真实旧版程序的回归基线，不能当作普通旧发布包清理；artifacts/step5-performance、step5 日志及 storage-v3-20260922 目录中的报告也保留在原处。

旧审计、旧窗口测试数据、截图、早期发布包及日志已压缩到本机 archive/2026-09-29/historical-files.zip，包内保留原来的相对路径。历史报告仍按测量时的路径记录结果，可在同目录的 archive-files.csv 中查找对应文件。恢复时先解压到单独目录，再按需取回文件；归档索引和校验结果见该目录的 README.txt。

编译缓存 bin、obj 和 artifacts/test-build 可以重新生成。清理后首次运行测试需要使用完整的 dotnet run 命令，构建完成后才能使用 --no-build。归档目录与本机生成产物一样不提交到 Git。

## 验证记录

本次更新后，**192 项自动测试通过**，本机运行约 26–28 秒。也实际检查了旧数据升级、撤销与重做、自动保存，以及关闭时保存失败后的恢复。正式软件包已确认不含测试工具；目前只在本地生成，尚未上传可下载版本。具体检查范围见 [测试报告](docs/TEST-REPORT.md)，速度变化和测量方法见 [性能报告](docs/PERFORMANCE.md)。
