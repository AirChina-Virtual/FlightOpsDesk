# VamSys.App — FlightOps Desk 桌面界面

这个项目负责你看到和操作的窗口，编译后生成 FlightOpsDesk.exe。文件夹和代码内部仍使用 VamSys.App 这个旧名字，避免仅仅改名就影响其他代码。

## 这个项目负责什么

使用 C#、WinUI 3 和 CommunityToolkit.Mvvm，目标为 net10.0-windows10.0.22621.0，发布 Windows 11 x64 自包含目录。直接引用 Infrastructure，由其接入 Core 业务层。

它处理点击按钮、编辑表格、选择文件和显示进度。真正发送请求、判断服务器是否完成操作以及保存数据库的工作，交给其他项目。用户的修改仍然要经过检查和审阅，不能从页面直接绕过这些步骤提交。

## 文件与交互分工

| 文件 | 具体作用 |
|---|---|
| App.xaml.cs | 启动程序；防止两个窗口同时使用同一份数据；记录和显示启动错误 |
| MainWindow.xaml | 融合标题栏、工作区选择、导航、资源入口与状态栏 |
| MainWindow.xaml.cs | 切换页面、接收表格修改，并让各次保存按顺序完成 |
| MainWindow.Api.cs | 连接服务器、刷新数据、提交修改；填写待核对的记录 ID 并恢复任务 |
| MainWindow.Fleets.cs | 当前工作区内的机型单选、多选及批量分配预览 |
| MainWindow.Closing.cs | 关闭时暂时禁用操作并保存；失败后允许继续编辑和重试 |
| MainWindow.Tasks.cs | 只创建可见的任务卡片和详情行；保存成功后更新发生变化的内容 |
| TaskViewModels.cs | 记住每条任务的显示内容和数量，避免每次进度变化都重新统计全部任务 |
| MainWindow.PageState.cs | 页面状态保存，支持返回页面时恢复交互位置 |
| MainWindow.Localization.cs、UiLocalization.cs | 即时切换中英文文字，并为读屏软件提供控件名称 |
| ViewModels.cs | 显示和编辑表格内容，在切换语言后更新提示 |
| MainWindow.Verification.cs | 仅在专用测试版本中启用，用来暂停保存、模拟故障和检查窗口 |
| Assets | SVG、PNG 和多尺寸 ICO 品牌资源 |

## 修改代码时要保留的行为

关闭分为“正常使用、正在保存、已关闭”三个阶段。用户点击关闭后，先禁止继续编辑，再等待保存完成。保存失败时窗口保持打开，草稿还在，并允许继续操作。这样可以避免保存期间新输入的内容丢失。

任务进度必须先保存成功，再显示到页面。界面可以把 50 毫秒内的多次变化一起显示，但不能因此推迟保存。页面只显示已保存的结果，不能提前显示还在处理中的下一步。

切换语言时只更新文字，不重新打开任务页，所以选中的记录和滚动位置可以保留。软件名始终为 FlightOps Desk；服务器字段名和用户自己输入的数据保持原样。

## 构建与窗口验证

从仓库根目录使用 PowerShell 7：

~~~powershell
./scripts/build.ps1 -Publish
~~~

入口为 artifacts/flightops-app/FlightOpsDesk.exe。同目录中的 PRI、XBF、Assets 和运行库均需保留。

~~~powershell
./scripts/test-localization-ui.ps1 -DataDirectory "$PWD/artifacts/ui-isolated"
~~~

窗口测试需要可操作的 Windows 桌面，并使用单独的测试数据目录。运行前先关闭正在使用的程序。模拟保存故障等检查需要专用测试版本；正常交付的程序不启用 UiVerification。

## 品牌与兼容

EXE 品牌元数据、标题栏和系统窗口使用 FlightOps Desk；图标原始文件为 Assets/FlightOpsDesk.svg。默认数据目录仍为 %LOCALAPPDATA%/VamSysBatch，环境变量仍为 VAMSYS_DATA_DIR。

[品牌说明](../../docs/BRANDING.md) · [实际窗口验证记录](../../docs/TEST-REPORT.md) · [项目首页](../../README.md)
