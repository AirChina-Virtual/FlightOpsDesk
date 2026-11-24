# FlightOps Desk

<img src="src/VamSys.App/Assets/FlightOpsDesk.png" width="96" height="96" alt="FlightOps Desk 图标">

FlightOps Desk 是一款 Windows 桌面工具，供虚拟航空公司（VA）通过 vAMSYS 的 Operations API 批量维护机场、机型、飞机、航路和航线。数据和草稿保存在本机；提交前统一审阅，提交后逐条读回核对。

**连接航空公司 → 读取最新数据 → 修改 → 审阅并提交 → 在任务中心核对结果**

C# · .NET 10 · WinUI 3 · Windows 11 x64 · 简体中文 / English

## 当前状态（2026-11-24）

- **最新版本**：1.2.0（2026-11-24），第一个正式版，可以在本仓库 GitHub 页面的 Releases 下载；之前的 1.0.0、1.1.0 是预发布。
- **开发进度**：重构 R0–R8 与综合优化计划的阶段 0–7 已完成。重新打开审阅页后第一次提交前检查仍有 150–200 ms 停顿（WinUI 要求的完整回收），作为已知成本接受。
- **真实环境验收**：在真实 vCCA 上，只针对本次新建的记录，做完后全部清理。
  - 2026-11-16：五类资源的新建、修改、删除，以及飞机跨机队移动。
  - 2026-11-16、11-18：scheduled 航线机队集合的追加、移除和替换。
  - 2026-11-18：取证服务器会怎样改写写入的值，以及各类航线删除后的表现。

  每项结果见 [能力验证矩阵](docs/CAPABILITY-MATRIX.md)。
- **自动检查**：每次推送都由云端 CI 构建并运行单元测试和集成测试。窗口测试和性能测量会占用桌面，在本机运行。
- **范围**：程序只管理运控数据，不做油量计算、天气简报等签派工作。

## 主要功能

- **多航空公司工作区**：每家 VA 的数据放在独立的工作区，连接凭据用当前 Windows 用户的 DPAPI 加密。
- **大表格编辑**：
  - 搜索、筛选、排序都作用于全部记录。每页 100／200／500 条，可以跨页选择。
  - 单元格当场检查：有问题的加红框，鼠标停留时说明原因。
  - 每种资源的列按固定顺序显示，常用的列在前（如飞机先显示注册号、名称、机队，航线先显示呼号、班号、始发、到达）。
  - 支持撤销和重做，有舒适和紧凑两种密度，隐藏的列重启后仍保持隐藏。5 万条航线时，翻页、编辑和滚动依然流畅。
- **批量操作**：批量规则、分配机型、删除与恢复、到期停用，都只处理已选的记录，执行前先预览改动数量。
- **审阅与提交**：
  - 整个工作区的改动按资源分组，每条写明操作和记录内容。
  - 可以按资源勾选，只提交其中一部分。
  - 确认框显示预计的请求次数和耗时。
- **提交前拦截**：冲突、逐行校验问题、未获准使用呼号前缀的机队，只挡住相关的修改，其他修改照常提交。
- **查重与 vAMSYS 一致**：同一对机场、同一班号可以有多条不同时刻、班期或日期的航线；新航线只有这些也都相同时才算疑似重复。
- **提交后核对**：
  - 每次写入后都读回核对。服务器的改写（转大写、去掉首尾空格等）若已有真实证据登记，算作一致。
  - 结果不明的写入只读核对，绝不自动重发；程序中断后能分清哪些请求“从未发出”，哪些“可能已发出”。
- **冲突处理**：比较上次读取的数据、你的草稿和服务器最新数据，修改的不是同一字段时自动合并，改了同一字段时由你选择保留哪一方。
- **CSV**：导入后先生成待审阅的修改；导出只包含有变化的记录，并保留程序不认识的列。
- **诊断与维护**：导出脱敏诊断包；先备份再清理工作区；可以更改数据存放位置。
- **中英文界面**：随时切换，不用重启。

## 使用

1. 从 Releases 下载 Windows 11 x64 正式包（如 `FlightOpsDesk-1.2.0-win-x64.zip`，也可以按下文自己生成），解压到单独的文件夹，运行 `FlightOpsDesk.exe`。程序包已带齐运行库。
2. 新建工作区，在“连接与设置”里填写 vAMSYS 提供的 Operations Client ID 和 Secret，测试连接后点“从 API 刷新”。
3. 在“数据管理”里修改，在“变更审阅”里检查并提交，在“任务中心”查看结果。

没有 API 账号时，可以用演示工作区，或者离线导入、导出 CSV；演示操作不会改动真实数据。GitHub 上的“Download ZIP”是源代码，不能直接运行。

详细说明见 [使用指南](docs/USER-GUIDE.md) 和 [升级与恢复](docs/STORAGE-MIGRATION.md)，改动记录见 [更新日志](CHANGELOG.md)。

## 从源代码生成程序

需要 Windows、.NET SDK 10.0.401，以及 Windows SDK／WinUI 构建环境。脚本优先使用 `.tools/dotnet` 里的 SDK，没有时用系统安装的 dotnet。以下命令在 PowerShell 7 中运行：

~~~powershell
./scripts/build.ps1 -Publish                 # 先跑全部自动测试，再生成正式包到 artifacts/flightops-app
./scripts/build.ps1 -Publish -UiVerification # 生成 QA 包到 artifacts/v3-qa，只用于隔离测试
pwsh -File scripts/check-all.ps1             # 只跑自动测试（快速模式）；-Mode Full 另跑 Debug 和分析器基线
~~~

- **正式包**：只含运行文件、使用和升级说明以及 CSV 示例，不含 QA 入口、故障注入、测试工具、开发文档和 PDB。
- **两种包**：都会检查程序集里的测试入口、包内文件清单和文档链接，检查通过后才替换输出目录。
- **窗口测试**：会接管鼠标和键盘，要单独运行，命令见 [窗口测试说明](tests/VamSys.UiTests/README.md)。
- **PowerShell 5.1**：`build.ps1` 含中文字面量，且保存为无 BOM 的 UTF-8，5.1 无法读取，请用 PowerShell 7。

图标的源文件和重新生成方法见 [名称和图标说明](docs/BRANDING.md)。

## 仓库结构

| 目录 | 内容 |
|---|---|
| [src/VamSys.Domain](src/VamSys.Domain/README.md) | 业务规则：字段规格、数据检查、修改清单、三方合并、撤销历史、CSV；不做 I/O |
| [src/VamSys.Application](src/VamSys.Application/README.md) | 会话与用例：保存、批次执行、检查点、关闭流程，以及存储和在线访问的接口 |
| src/VamSys.Presentation | 表格、对话框和任务页的视图模型，不依赖 WinUI |
| [src/VamSys.Storage](src/VamSys.Storage/README.md) | 本机 SQLite 数据库：保存、读取、升级和备份 |
| [src/VamSys.Infrastructure](src/VamSys.Infrastructure/README.md) | 连接 vAMSYS：请求、限流、回读核对、判重和中断恢复 |
| [src/VamSys.App](src/VamSys.App/README.md) | WinUI 窗口、数据表格、审阅、任务中心、多语言和图标 |
| [tests](tests/README.md) | 单元、集成、窗口测试和性能基准 |
| [tools/VamSys.QaSeed](tools/VamSys.QaSeed/README.md) | 打包检查、测试数据、只读验证、真实写入取证、性能比对 |
| scripts | 生成程序、跑检查、生成图标 |
| [docs](docs/README.md) | 使用说明、参考资料、测试与性能报告；`docs/history` 保存已完成的计划和旧报告 |
| examples、packaging | CSV 示例，以及随包附带的说明 |

依赖方向和改动时的注意事项见 [架构速览](docs/ARCHITECTURE.md)。

## 本地产物与归档

`artifacts`、`archive`、`.tools` 以及各项目的 `bin`、`obj` 只保存在本机，不提交到 Git。

- **artifacts**：
  - `flightops-app`（正式包）和 `v3-qa`（QA 包）；
  - `test-build`、`ui-tests`（窗口测试的数据目录），以及 `write-evidence`（真实写入取证报告）。
  - `step5-v3-baseline`、`r0-baseline`、`r0-real-db` 是旧版程序、重构前性能和真实数据库副本的基线，测试和性能比对要用，不能当作普通产物清理。
- **archive**：按日期存放归档内容，每个目录都有说明、逐文件 SHA-256 清单和清理记录。报告里提到的旧 artifacts 路径，可以在这些清单中查到。
- 可以重新生成的内容直接删除，不归档。
