# FlightOps Desk

<img src="src/VamSys.App/Assets/FlightOpsDesk.png" width="96" height="96" alt="FlightOps Desk 图标">

FlightOps Desk 是一款 Windows 桌面工具，帮助虚拟航空公司在 vaMSYS 中一次处理大量机场、机型、飞机、航路和航线数据。它通过 vaMSYS 的 Operations API 读取和提交数据，省去逐条手动修改。

**连接航空公司 → 读取最新数据 → 修改 → 审阅修改清单 → 提交 → 再次读取，确认结果**

C# · .NET 10 · WinUI 3 · Windows 11 x64 · 简体中文 / English

## 当前状态

程序可以在本地保存数据和草稿、记录任务进度，并处理本地修改与服务器数据不一致的情况。5 万条航线时，数据页的翻页、编辑、撤销和滚动都能保持流畅，数字见 [性能报告](docs/PERFORMANCE.md)。

已经使用真实 vCCA 账号完成独立 API 脚本测试，包含仅针对自建记录的增删改和清理。软件实际适配器、执行器和存储另有模拟回归；这些证据不能代替软件窗口与真实写入流程验收。各能力分别登记在 [能力验证矩阵](docs/CAPABILITY-MATRIX.md)，已知服务端异常及未验证操作仍有独立保护。程序主要管理运控数据，不提供油量计算、天气简报等完整签派功能。

## 主要功能

- **多航空公司工作区。** 每家虚拟航空公司（VA）的数据放在独立的工作区；连接密钥由当前 Windows 用户的加密机制保护。
- **大表格编辑。** 搜索、筛选和排序作用于全部数据。默认每页 200 条，可选 100／200／500 条，支持跨页选择，以及舒适／紧凑两种密度。单元格点击后即可编辑；撤销和重做只保存、只刷新受影响的行。
- **批量操作。** 只处理已选记录；预览会显示已选、跳过、未变化和实际变化的数量。
- **统一审阅。** 按资源分组审阅整个工作区，明确区分本次提交、暂缓和受阻的项；页面、确认框和任务使用同一份清单。
- **CSV。** 导入后先检查准备修改的内容；导出只输出变化的记录，并保留程序不认识的列。
- **冲突处理。** 服务器数据也被修改时，比较原始数据、你的修改和服务器最新数据；有冲突时请你确认。
- **中断恢复。** 持续保存任务进度。请求超时或程序中断后，先检查服务器是否已完成操作，避免重复创建或重复修改。
- **中英文界面。** 可以即时切换，不需要重启。
- **批准关联与航线机队集合。** 新机队选择一个已批准、未受限的航司参数；scheduled 航线可预览替换、追加或移除机队，提交前检查批准前缀，最终集合必须非空。
- **已验证范围开放。** 五类资源中有独立真实请求和回读证据的字段范围解除首次样本限制；飞机支持单独跨机队移动，软删除支持完整 204／404 证据核对及历史任务只读补核对。范围见 [证据清单](docs/VERIFIED-API-SCOPE.md)。
- **诊断与工作区维护。** 导出脱敏诊断及 SHA-256；在设置页先备份整个数据库，再事务清理当前工作区，保留连接设置和凭据。

## 快速开始

1. 准备 Windows 11 x64 程序包，解压到单独的文件夹。程序包自带所需运行库。
2. 为虚拟航空公司创建工作区，在“连接与设置”填写 vaMSYS 提供的 Operations Client ID 和 Secret。
3. 测试连接并点击“从 API 刷新”。在“数据管理”中修改，在“变更审阅”中检查后提交，在“任务中心”查看结果。

GitHub 的“Download ZIP”下载的是源代码，不能直接运行；生成程序的方法见下文。没有 API 账号时，可以使用演示工作区，或者离线导入、导出 CSV；演示操作不会修改真实数据。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md) · [文档导航](docs/README.md) · [更新日志](CHANGELOG.md)

## 开发者：从源代码生成程序

需要 Windows、.NET SDK 10.0.401，以及 Windows SDK／WinUI 构建环境。脚本优先使用 `.tools/dotnet` 中的 SDK，没有时使用系统安装的 dotnet。

在 PowerShell 7 中运行：

~~~powershell
./scripts/build.ps1 -Publish                 # 先运行全部自动测试，再生成正式包到 artifacts/flightops-app
./scripts/build.ps1 -Publish -UiVerification # 生成 QA 版本到 artifacts/v3-qa，仅用于隔离测试
~~~

正式包只包含运行文件、使用和升级说明以及 CSV 示例；不包含 QA 控制入口、故障注入、测试工具、开发文档或 PDB。两种发布都会检查程序集中的测试入口和包内文件清单，检查通过后才替换各自的输出目录。

只运行自动测试：

~~~powershell
dotnet run --project tests/VamSys.Tests/VamSys.Tests.csproj -c Release -p:RestoreLockedMode=true
~~~

`build.ps1` 和部分窗口脚本含有中文字面量，并且保存为无 BOM 的 UTF-8，Windows PowerShell 5.1 无法正确读取它们。只有 5.1 时，可以直接运行上面的 dotnet 命令，发布参数见 [性能报告](docs/PERFORMANCE.md#测量方法与复现)。图标源文件和重新生成方法见 [品牌说明](docs/BRANDING.md)。

## 仓库结构

| 目录 | 内容 |
|---|---|
| [src/VamSys.App](src/VamSys.App/README.md) | 窗口、数据表格、审阅、任务、多语言和图标 |
| [src/VamSys.Core](src/VamSys.Core/README.md) | 判断数据怎样修改和检查、撤销历史、生成修改清单 |
| [src/VamSys.Infrastructure](src/VamSys.Infrastructure/README.md) | 连接 vaMSYS、查找重复记录、在本机保存数据 |
| [tests/VamSys.Tests](tests/VamSys.Tests/README.md) | 自动测试、窗口脚本和测量方法 |
| scripts | 生成程序和图标、自动操作窗口、测量性能 |
| docs | 使用方法、API 支持情况、测试与性能报告、升级说明；history 保存历史报告 |
| examples | CSV 示例 |
| packaging | 正式版和 QA 版随包附带的说明 |
| artifacts、archive、.tools | 本机生成的程序包、归档和 SDK，不提交到 Git（见下节） |

## 本地产物与归档

artifacts、archive、`.tools`、bin 和 obj 都只保存在本机，不会提交到 GitHub。

- **artifacts**
  - flightops-app：当前正式包；2026-10-20 审阅修复版通过 255 项回归和包检查。该包构建于阶段 1（解除卡死）之前，不包含其修改；需要时用 `build.ps1 -Publish` 重新生成。
  - FlightOpsDesk-2026-10-20-review-fixes-win-x64.zip 及 .sha256：当前正式交付压缩包与校验文件；上一版 verified-scope ZIP 已归档到 archive/2026-10-20-review-fixes。
  - step5-v3-baseline：真实旧版程序的回归基线，测试会用到，不能当作普通旧包清理。
  - v3-qa、test-build 和各项目 bin/obj：可重建输出，本次整理已删除，后续发布或构建会重新生成。
- **archive**：按日期存放已归档的内容。每个目录都有说明、逐文件 SHA-256 清单（*-files.csv）、ZIP 校验值及清理记录。2026-10-19-performance 保存此前测量、窗口结果和旧包；2026-10-20-cleanup 保存本次被替换的正式包，以及 core-performance（含修改前源码）、checkbox-theme、encoding-verification、cache-cleanup、repository-metadata、step5-performance、storage-v3-20260922 和 step5 日志。
- **阶段 1 提交后整理**：删除构建输出（src／tests 的 bin、obj，artifacts/test-build）和测试生成的 artifacts/step5-performance，共 1,503 个文件、约 601 MB，均可重新生成；逐文件清单在 archive/2026-10-20-phase1-cleanup。
- **此前的构建后整理**：archive/2026-10-20-startup-fix-cleanup 保存 152 个项目文件的清理前备份、未提交差异、重新生成的 6 份 step5-performance 结果、逐文件清单与删除记录。已删除重新构建产生的 test-build 及 src/tests 的 bin/obj；当前正式目录、ZIP、SDK 和旧版兼容性基线保留。
- **恢复**：先把 ZIP 解压到单独的目录，再按清单取回需要的文件。报告中提到的旧 artifacts 路径，都可以在清单中查到。

可以重新生成的内容会直接删除，不归档，包括合成测试数据库、各轮 QA 构建，以及调试用的内存转储；删除记录保存在各归档目录的 cleanup.csv。

## 验证记录

**260 项自动测试通过**（没有本机旧版基线时为 259 项），使用隔离合成数据。最近修复了未发出或被认证拒绝的写入被误标为未知、Unknown 无法解除、旧版两步新建无法续传、空 VA 无法连接和数字 Client ID 改写诊断正文，此前还修复了短数字 Client ID 下删除自动核对失败、提交保存失败后任务残留和 Unknown 审阅重复执行，均有对应回归；此前 15 项范围开放、飞机移动、删除只读核对和持久化回归，此前重量、诊断、批准、jumpseat、维护与启动交接回归保留。新增界面没有重新做窗口视觉验收，本轮没有执行软件真实业务写入。独立真实 API 结果和软件验证分别记录，详见 [测试报告](docs/TEST-REPORT.md)。
