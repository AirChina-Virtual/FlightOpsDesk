# VamSys.App — FlightOps Desk 桌面界面

本项目是 Windows 用户直接运行的程序，输出 FlightOpsDesk.exe。保留 VamSys.App 项目目录和命名空间，以减少品牌更名对内部引用的影响。

## 职责与依赖

使用 C#、WinUI 3 和 CommunityToolkit.Mvvm，目标为 net10.0-windows10.0.22621.0，发布 Windows 11 x64 自包含目录。直接引用 Infrastructure，由其接入 Core 业务层。

本层负责交互、显示状态、文件选择和命令编排；API 字段转换、未知写入判断及 SQLite 事务由下层实现。页面不能直接拼接 API 请求来绕过变更审阅。

## 文件与交互分工

| 文件 | 具体作用 |
|---|---|
| App.xaml.cs | 启动、数据目录单实例租约、启动异常记录与错误窗口 |
| MainWindow.xaml | 融合标题栏、工作区选择、导航、资源入口与状态栏 |
| MainWindow.xaml.cs | 页面编排、编辑事件、自动保存及共用保存队列 |
| MainWindow.Api.cs | 连接、刷新、在线审阅提交、候选 ID 更正和恢复入口 |
| MainWindow.Fleets.cs | 当前工作区内的机型单选、多选及批量分配预览 |
| MainWindow.Closing.cs | 关闭期间冻结交互、停止自动保存及失败后恢复 |
| MainWindow.Tasks.cs | 虚拟化任务卡片／详情、已提交进度通知和局部刷新 |
| TaskViewModels.cs | 稳定任务与记录模型、索引、计数和已提交显示快照 |
| MainWindow.PageState.cs | 页面状态保存，支持返回页面时恢复交互位置 |
| MainWindow.Localization.cs、UiLocalization.cs | 双语资源绑定、即时刷新和无障碍名称 |
| ViewModels.cs | 表格行／单元格显示、编辑入口与本地化通知 |
| MainWindow.Verification.cs | 仅 UI_VERIFICATION 构建包含的受控窗口验证通道 |
| Assets | SVG、PNG 和多尺寸 ICO 品牌资源 |

## 关键交互约束

关闭状态为 Open → Saving → Closed。第一次等待前冻结编辑，保存失败时保留草稿并恢复交互；不能在保存等待期间继续修改工作区。

任务通知仅在检查点持久化成功、必要的内存重基线完成后发布。最多 50 ms 的合并只影响显示，不推迟数据库提交。视图模型保存已提交的显示快照，避免下一次未提交状态提前出现在页面中。

语言切换使用绑定／局部更新，不重建整个任务页；保留选择和滚动位置。软件名始终为 FlightOps Desk，官方字段和用户数据不翻译。

## 构建与窗口验证

从仓库根目录使用 PowerShell 7：

~~~powershell
./scripts/build.ps1 -Publish
~~~

入口为 artifacts/flightops-app/FlightOpsDesk.exe。同目录中的 PRI、XBF、Assets 和运行库均需保留。

~~~powershell
./scripts/test-localization-ui.ps1 -DataDirectory "$PWD/artifacts/ui-isolated"
~~~

窗口脚本需要交互式 Windows 桌面，会创建隔离测试数据。不要与用户正在编辑的程序实例混用。生产发布不设置 UiVerification；受控关闭故障和大任务 UI 验证使用独立 QA 构建。

## 品牌与兼容

EXE 品牌元数据、标题栏和系统窗口使用 FlightOps Desk；图标原始文件为 Assets/FlightOpsDesk.svg。默认数据目录仍为 %LOCALAPPDATA%/VamSysBatch，环境变量仍为 VAMSYS_DATA_DIR。

[品牌说明](../../docs/BRANDING.md) · [实际窗口验证记录](../../docs/TEST-REPORT.md) · [项目首页](../../README.md)
