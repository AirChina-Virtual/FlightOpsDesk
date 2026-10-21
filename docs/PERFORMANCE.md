# 速度与资源占用报告

本文记录当前版本在大量数据下的表现、各项优化的效果和测量方法。逐轮的详细测量保存在 [历史记录](history/PERFORMANCE-2026-09-22-to-2026-10-19.md)。

全部数字来自同一台机器（Intel Core Ultra 9 185H，Windows build 26300，显示缩放 150%），使用隔离的合成数据：演示工作区包含全部 20 个航线字段和 20 步撤销历史。不连接真实 API。单机测量会受 JIT、GC、磁盘缓存和后台负载影响，不代表其他机器上的速度。

## 当前表现（2026-10-19）

| 场景 | 2 万行 | 5 万行 |
|---|---:|---:|
| 启动到首帧 | 约 1.8 s | 约 2.5 s |
| 其中读取工作区 | 约 0.47 s | 约 1.1 s |
| 打开数据页（查询 + 200 条） | 约 0.33 s | 约 0.44 s |
| 全字段搜索 | 约 0.13 s | 约 0.32 s |
| 翻页 P95 | 约 9 ms | 约 8 ms |
| 切换语言 P95 | 约 24 ms | 约 30 ms |
| 单格编辑处理 P95 | 约 1 ms | 约 1 ms |
| 撤销（含保存与页面刷新）中位／P95 | 11／198 ms | 10／73 ms |
| 纵向滚动每帧中位／P95（每帧 3 行） | 约 18／25 ms | 约 18／25 ms |
| 离开／返回工作区 | 约 0.9／0.6 s | 约 2.4／1.2 s |
| 打开数据页后的工作集 | 约 350 MiB | 约 520 MiB |
| 5 轮切换工作区后的工作集 | 约 680 MiB | 约 790 MiB |

审阅页（演示模式，修改前 N 条航线）：

| 变更数 | 打开审阅页：总时长／界面最长停顿 | 提交前检查：总时长／界面最长停顿 |
|---:|---:|---:|
| 1,000（5 万行） | 505／54 ms | 463／33 ms |
| 10,000（5 万行） | 521／34 ms | 542／48 ms |
| 50,000（5 万行） | 1,160／253 ms | 1,193／319 ms |

翻页、语言、编辑和撤销的计时都包含原生绑定与同步布局，均为 5 次预热、20 次采样；首次展示和搜索为单次计时。滚动帧指相邻两次合成帧之间的间隔，包含界面线程上的容器实现、绑定和布局，不代表 GPU 呈现时间。

## 本轮优化（2026-10-19）

| 优化 | 主要变化 | 修改前 → 修改后 |
|---|---|---|
| 撤销／重做只保存变化的行 | 只替换已有行的步骤只写这些行和撤销栈顺序 | 5 万行撤销中位 2,648 → 130 ms |
| 撤销后只刷新受影响的行 | 不再重新查询全部数据，只重绑当前页上受影响的行；只记录任务或连接状态的保存不再让数据页失效 | 5 万行撤销中位 130 → 10 ms，P95 620 → 73 ms |
| 查询期间不切换表格 IsEnabled | 改为屏蔽输入并降低不透明度 | 关闭窗口不再因为积压的 IsEnabledChanged 事件拖延数十秒 |
| 表格单元格轻量化 | 平时只显示文本，编辑时才创建 TextBox；模板改为编译绑定（x:Bind）；行容器回收时复用单元格控件；去掉每个单元格的 ToolTip | 纵向滚动每帧中位 55 → 18 ms；横向 43 → 4 ms；翻页 P95 52–82 → 8–9 ms；滚动带来的内存增长 414 → 196 MiB |
| 切换工作区的内存 | 单元格闭包不再直接引用工作区；切换时释放旧工作区的视图缓存和 API 会话 | 5 轮切换后工作集：2 万行 1,096 → 704 MiB，5 万行 1,478 → 799 MiB |
| 切换时复用数据页 | 切换工作区或资源时只更换内容，不重建页面 | 资源来回切换 20 次的原生增长 374 → 168 MiB |
| 审阅页后台构建 | 审阅计划和提交前检查在后台构建；内容键改为缓存的 SHA-256 | 5 万项变更：提交前界面冻结约 1.8 s → 最长停顿约 0.3 s |

## 已知的剩余成本

- **5 万项变更的审阅。** 重建审阅计划会复制每个变更行的前后版本，约分配 250 MB。由此产生的 GC 暂停仍会让界面停顿 250–320 ms。
- **切换工作区。** 每轮往返仍增长约 6 MiB 原生内存，来自切换前的完整保存与表格重新显示同时发生。改为切换时只保存待写的单元格编辑可以去掉这部分，但需要先确认没有任何改动绕过“修改后立即完整保存”的约定，因此尚未修改。
- **启动和工作区读取。** 5 万行时读取工作区约 1.1 s，在界面线程上进行，每次分配约 530 MB。
- **完整保存。** 撤销结构步骤、批量操作和切换工作区仍会完整保存。序列化在界面线程上进行，5 万行时约 2 s。

后续方向见 [代码优化计划](OPTIMIZATION-PLAN.md)。

## 在线请求与本地计算（保持不变）

- 限流预算：进程内所有 VA 合计每分钟最多 60 次请求，间隔至少 1 秒，遵守 Retry-After。
- 判重：同一机场对的新建只查询一次候选列表。机场、机型、飞机在完整读取成功后建立索引，确认写入后同步更新。
- 撤销历史只记录变化的行。2 万行时，新增一步的保存量约 1.5 KB；编辑单元格后的自动保存只写对应的行。
- 在线任务结束后，会把请求量、限流等待和保存统计写入数据目录的 `performance/<工作区>/<任务>/<执行>.json`，其中不包含凭据或请求内容。

## 测量方法与复现

窗口测量需要通过 `UiVerification=true` 构建 QA 版本，并使用隔离的数据目录。脚本会自行生成合成数据，只操作自己启动的进程。

~~~powershell
# QA 版本（也可用 ./scripts/build.ps1 -Publish -UiVerification，需要 PowerShell 7）
./.tools/dotnet/dotnet.exe publish src/VamSys.App/VamSys.App.csproj -c Release -p:Platform=x64 -p:UiVerification=true -p:VamSysTestSupport=true -o artifacts/v3-qa

# 数据页：打开、搜索、翻页、语言、编辑、撤销
./scripts/test-data-performance-ui.ps1 -Rows 50000 -Executable "$PWD/artifacts/v3-qa/FlightOpsDesk.exe"

# 启动、工作区读取与切换、审阅、逐帧滚动、内存
./scripts/test-baseline-ui.ps1 -Rows 50000 -Executable "$PWD/artifacts/v3-qa/FlightOpsDesk.exe"

# 核心计算基准（不含窗口和数据库）
./.tools/dotnet/dotnet.exe run --project tests/VamSys.Tests -c Release -- --benchmark-core artifacts/core-performance/current.json
~~~

本轮的原始结果（JSON、截图、日志）已压缩到本机 `archive/2026-10-19-performance/performance-round-results.zip`，原路径和 SHA-256 记录在同目录的 `performance-round-results-files.csv`。历史记录中提到的 artifacts 路径都可以在这里查到。

2026-10-20 整理后，`artifacts/core-performance`（包括 baseline-src）、step5 性能／构建日志与 storage-v3 报告另归档到 `archive/2026-10-20-cleanup/historical-results.zip`，原路径和逐文件哈希见同目录 `historical-results-files.csv`。上面的命令仍可重新生成当前基准和 QA 包；现有正式包位于 `artifacts/flightops-app`，用于兼容性测试的 `artifacts/step5-v3-baseline` 继续原位保留。

任务启动修复的 238 项回归重新生成了 `artifacts/step5-performance` 下 6 份 JSON；这些结果已单独归档至 `archive/2026-10-20-startup-fix-cleanup/step5-performance-results.zip`，原路径和 SHA-256 见同目录 `step5-performance-results-files.csv`。本轮构建缓存已删除，基准与测试命令仍可重新生成它们。
