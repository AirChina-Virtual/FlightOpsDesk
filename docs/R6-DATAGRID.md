# R6 数据表组件方案

计划第 R6 阶段第 5 步：数据表重建为独立组件。本文给出分步做法、必须保留的设计和验收门槛。

## 现状

数据页的表格逻辑分散在 `MainWindow.Data.cs`（约 580 行）、`MainWindow.DataLayout.cs`、`EditableCell.cs`、`DataRowPresenter.cs`、`DataTableTemplates.xaml`、`CellTemplateSelector.cs`，以及 Presentation 中的 `CellViewModel`、`RowViewModel`、`DataTableViewModel`。窗口直接持有行池、选择计数、翻页按钮和锁表状态。

## 必须保留的设计

下列设计都有实测依据，原因见代码注释与 [性能报告](PERFORMANCE.md)。

| 设计 | 原因 |
|---|---|
| 只为当前页建行模型；行数不变时池中行原地重绑，不换 `ItemsSource` | 换 `ItemsSource` 会重建全部单元格控件，并为丢弃的控件保留原生内存 |
| 不用可观察集合作为行源 | 实测翻页和换语言的布局时间翻倍（P95 10 → 20 ms、25 → 56 ms） |
| 行容器回收时保留单元格控件，只换数据上下文；重绑不触发编辑 | `ItemsControl` 在回收行换单元格列表时会重建全部单元格控件 |
| 单元格平时是文本，开始编辑时才建输入框 | 2 万／5 万行的内存与滚动帧时间 |
| 锁表用 `IsHitTestVisible` 与透明度，不切 `IsEnabled` | `IsEnabled` 级联到所有单元格，事件队列只增不减，关窗时要花数分钟清空 |
| 编辑回调经 `RowLink` 读取当前工作区与资源，不直接捕获 | 回收行很晚才被终结，直接捕获会让旧工作区一直留在内存中 |
| 数据页控件跨工作区与资源保留，只换内容 | 每个丢弃的数据页约保留 10 MiB 原生内存 |
| 编译绑定 | 行实现、回收、重绑时没有反射 |
| AutomationId、读屏名称、键盘行为（Enter／Esc／Tab、跨页选择）不变 | 窗口脚本与读屏用户依赖 |

## 分步

**D1 表格模型移入 Presentation（行为不变）。** 新建 `DataGridViewModel`，接管以下内容：

- 行池与重绑规则；
- 单元格创建与编辑提交：`ResourceData.Edit`、改动计数增量、`RecordCellEdit`；
- 只读规则、行状态与机队标签文字；
- 选择与选中计数；
- 翻页、选择菜单各控件的可用状态（作为属性）。

窗口改为调用它，旧控件不变。每部分都配无窗口测试。这一步不改界面，窗口脚本应全部照旧通过。

实际落点（2026-10-28）：单元格规则在 `DataGridRules`，选择计数与可用状态在 `SelectionCounts`／`DataGridAvailability`，编辑提交在 `CellEditCommitter`，行池、`RowLink` 与行、单元格模型的创建在 `DataGridViewModel`。窗口经 `IDataGridHost` 提供当前工作区、资源、基线、对话框和编辑后的通知（改动计数、`RecordCellEdit`、状态栏）。改动计数字典和选择恢复仍在窗口，D2 时随控件一起处理。

**D2 新 `DataGrid` 控件（仅 QA 构建可切换）。** 新建 `DataGrid.xaml` 用户控件：

- 列标题、虚拟化行列表、行呈现器（沿用 `EditableCell` 与 `DataRowPresenter` 的做法）；
- 页脚分页与选择；
- 锁表方式同上；
- 全部绑定到 `DataGridViewModel`。

在 `UiVerification` 构建中用隐藏开关 `VAMSYS_QA_DATAGRID=new` 选择新控件。正式包仍用旧控件，打包检查证明正式包不含开关。

进度（2026-10-28，第一块）：`DataGrid.xaml` 含横向滚动区、行列表与页脚；页脚（翻页、跳页、每页条数、选择菜单、取消查询）用编译绑定绑到 Presentation 的 `DataGridFooterViewModel`，AutomationId 与旧页脚相同。窗口经 QA 构建才实现的分部方法 `ChooseDataGrid` 选择控件；两条路径共用同一组翻页与选择方法。打包检查新增两项：`ChooseDataGrid` 只在 QA 包中存在，正式包程序集不含任何 `VAMSYS_QA_` 字符串。列标题、锁表、选择恢复与行填充仍由窗口对新控件的 `ListView` 执行，后续几块再移入控件。

**D3 并排测量。** 同机交替运行，新旧控件各 3 轮：

- `test-data-performance-ui -Rows 20000|50000`：打开、搜索、翻页、编辑、撤销、滚动；
- `test-cell-editing-ui`：点击、输入、Tab、Esc、读屏值与只读；
- `test-data-footer-ui -DataGrid old|new`：页脚翻页、跳页、每页条数、选择菜单与读屏名称；
- `test-ui-interactions -Rows 20000`：密度、布局、批量范围。

**D4 替换。** 新控件达到下面的门槛后，正式包改用新控件，删除旧控件、`MainWindow.Data*.cs` 中的表格代码和隐藏开关。

## 验收门槛

- 窗口脚本全部通过，新旧控件的 AutomationId 清单一致。
- 5 万行数据页的打开、搜索、翻页、编辑、撤销、滚动，各项中位数不慢于旧控件 10%。
- 原生内存增长不高于旧控件。
- 键盘与读屏逐项对照 `test-cell-editing-ui`，结果一致。
- 明暗主题、高对比度，以及 100％／150％／200％ 缩放，由人工查看。
