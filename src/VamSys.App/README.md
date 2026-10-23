# VamSys.App — FlightOps Desk 桌面界面

这个项目负责窗口和交互，编译后生成 `FlightOpsDesk.exe`。文件夹和命名空间仍使用 VamSys.App 这个旧名字，避免只为改名而影响其他代码。

使用 C#、WinUI 3 和 CommunityToolkit.Mvvm，目标框架为 net10.0-windows10.0.22621.0，发布为 Windows 11 x64 自包含目录，明确引用 Domain、Application 和 Infrastructure。界面负责点击、编辑表格、选择文件和显示进度；发送请求、判断服务器结果和保存数据库都交给其他项目。用户的修改必须经过检查和审阅，不能从页面直接绕过这些步骤提交。

## 文件分工

| 文件 | 作用 |
|---|---|
| App.xaml.cs | 启动程序；防止两个窗口同时使用同一份数据；记录并显示启动错误 |
| MainWindow.xaml、MainWindow.xaml.cs | 标题栏、工作区选择、导航和状态栏；切换页面和工作区；保存顺序；撤销／重做 |
| MainWindow.Data.cs | 数据页：后台查询、分页、跨页选择、当前页行模型复用、查询期间锁定表格 |
| MainWindow.DataLayout.cs、MainWindow.PageState.cs | 响应式页头、舒适／紧凑密度、表头；各工作区和资源的查询条件、页码与滚动位置 |
| DataTableTemplates.xaml | 行和单元格的数据模板，使用编译绑定（x:Bind） |
| DataRowPresenter.cs | 一行的单元格布局。行容器被回收给另一条记录时，保留单元格控件，只更换数据 |
| EditableCell.cs | 平时显示文本，点击或用 Tab 进入时才创建 TextBox；自动化接口仍报告为可编辑控件 |
| CellTemplateSelector.cs | 按字段选择文本单元格、机型按钮或状态文字 |
| ViewModels.cs、ViewModels.Xaml.cs | 行和单元格模型；ViewModels.cs 不依赖 WinUI，测试项目也会编译它 |
| DataTableViewModel.cs | 不依赖 WinUI 的完整结果 ID、查询、分页和已选 ID |
| MainWindow.Review.cs、ReviewPlanBuilder.cs | 工作区审阅：后台构建计划、内容键（SHA-256）、提交前检查；ReviewPlanBuilder 不依赖 WinUI |
| MainWindow.Submission.cs、TaskSubmission.cs | 在线／演示任务的持久化启动交接；解锁前选中任务中心，只启动本次明确提交的任务；TaskSubmission 链接进无窗口回归 |
| MainWindow.Batch.cs、BatchEditPreview.cs | 已选记录范围、实际变化计数、本地操作锁 |
| MainWindow.Fleets.cs | 机型单选、多选和批量分配预览 |
| MainWindow.Api.cs | 连接服务器、刷新数据、提交、填写待核对的 ID 并恢复任务 |
| MainWindow.Maintenance.cs | 只读参数加载、新机队选择一个参数、备份清理确认与会话注销 |
| MainWindow.Diagnostics.cs | 导出已持久化的脱敏任务诊断与 SHA-256 |
| MainWindow.DeleteVerification.cs | 显式只读核对 Unknown／人工确认删除项，不执行 Pending；失败重载本地状态 |
| MainWindow.Tasks.cs、TaskViewModels.cs | 任务页只创建可见内容，进度变化只更新相关记录 |
| MainWindow.Closing.cs | 关闭时冻结操作并保存；失败后恢复编辑并允许重试 |
| MainWindow.Localization.cs、UiLocalization.cs | 即时切换中英文，为读屏软件提供控件名称 |
| ../../tests/UiVerification/MainWindow.Verification.cs | 只编译进 QA 版本：窗口控制、故障注入和测量命令；正式程序不包含此文件 |
| Assets | SVG、PNG 和多尺寸 ICO 图标 |

## 修改代码时要保留的行为

**保存。** 编辑单元格后，700 毫秒内没有新的输入就自动保存，并且只写被编辑的行（`SaveEdits`）。只替换已有行的撤销或重做，也通过 `SaveEdits` 保存这些行和撤销栈顺序。其他会修改数据的操作，都必须在修改后立即调用 `Save()`；这是“数据库与内存一致”的保证，新增修改数据的操作时不能只依赖自动保存。`Save()` 现在是变更日志保存：由 `Workspace.Journal()` 比较数据库记录与内存，只写发生了变化的行、字段定义、冲突、任务和头部，也一并写入尚未保存的单元格编辑；什么都没变就什么都不写。保存时机不变，变的只是写入的字节数。关闭窗口和切换工作区同样调用 `Save()`，没有改动时不产生任何写入。只记录任务或连接状态、不改动任何行的保存，用 `Save(dataChanged: false, JournalScope.Jobs)`（只写任务）或 `JournalScope.Header`（只写工作区头部，如连接 API），这样不会让数据页失效，也不会写行；范围之外的待写变化留在日志里。原地修改任务或条目的代码（重置失败条目、演示执行器、演示收尾）必须先 `workspace.Journal().TouchJob(job)`，这是快速路径，漏标记时状态指纹也会让日志发现任务状态变化；经过 `store.CheckpointWriter` 的写入自动标记。冲突处理只在内存里应用，对话结束或取消时保存一次。确认任务是否已保存用 `store.JobExists`，导出诊断用 `store.LoadJob`，维护预览用 `store.CountRows`，都不加载整个工作区。

**数据页。** 只保留当前页的行和单元格模型。翻页、切换工作区或资源时，复用页面和行模型，只重新绑定；重新绑定不得触发编辑。单元格的回调经由 `RowLink` 读取当前的工作区和资源，不能直接捕获它们，否则切换后旧工作区无法回收。后台查询只读取界面线程分批捕获的不可变数据，完成后要核对取消状态、查询代次、工作区和资源，再应用结果。普通单格编辑和只替换行的撤销，只更新相关行的状态；下一次应用查询时，才重新筛选和排序。

**查询期间锁定表格时，不要切换 `IsEnabled`。** 这个属性会传到每个已实现的单元格，WinUI 会为每个控件排队一条事件；积压之后，关闭窗口要花很长时间。应改用 `LockDataTable`：屏蔽输入、降低不透明度、移走焦点。

**审阅。** 审阅计划在后台构建，期间用 `WhileLocked` 锁定编辑和导航。外层本地操作结束时，如果后台仍在读取工作区，要由后台任务负责解锁。提交前在后台重建计划，并与显示的计划比较内容键；有任何变化都要刷新清单，并要求重新审阅。

**任务。** 进度必须先保存成功，再显示到页面。界面可以把 50 毫秒内的多次变化合并显示，但不能因此推迟保存，也不能提前显示还在处理中的下一步。

**提交交接。** 在线与演示使用同一个 TaskSubmission：确认清单、保存 Pending 成功、在 LocalOperation 解锁前明确设置 tasks 页面，最后只启动本次保存的 job。不能只设置 Navigation.SelectedItem，因为 Navigate 在本地锁定时会忽略它；也不能在最终 Render 仍为 review 时启动执行。工作区对象、VA、模式或操作可用性变化时明确拒绝启动。重新加载、解锁或连接不得自动扫描历史 Pending。

**语言。** 切换语言时只更新文字，不重新打开页面，所以选中的记录和滚动位置会保留。软件名称始终为 FlightOps Desk；服务器字段名和用户输入的数据保持原样。

## 构建与窗口验证

在仓库根目录使用 PowerShell 7：

~~~powershell
./scripts/build.ps1 -Publish                # 正式包 → artifacts/flightops-app
./scripts/build.ps1 -Publish -UiVerification # QA 版本 → artifacts/v3-qa
~~~

两种发布都会先运行全部自动测试，再检查程序集中是否含有测试入口，以及包内的文件清单。QA 版本的编译目录独立于正式程序。入口为 `FlightOpsDesk.exe`；同目录中的 PRI、XBF、Assets 和运行库都需要保留。只有 Windows PowerShell 5.1 时，可以直接用 `.tools/dotnet/dotnet.exe publish` 生成，参数见 [性能报告](../../docs/PERFORMANCE.md#测量方法与复现)。

窗口脚本需要可操作的 Windows 桌面和单独的测试数据目录，运行前要关闭正在使用的程序。各脚本说明见 [Tests 项目说明](../../tests/VamSys.Tests/README.md#实际窗口检查)。

## 品牌与兼容

EXE 元数据、标题栏和系统窗口使用 FlightOps Desk；图标原始文件为 Assets/FlightOpsDesk.svg。默认数据目录仍为 `%LOCALAPPDATA%/VamSysBatch`，环境变量仍为 `VAMSYS_DATA_DIR`。

[品牌说明](../../docs/BRANDING.md) · [测试报告](../../docs/TEST-REPORT.md) · [项目首页](../../README.md)
