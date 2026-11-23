# VamSys.Benchmarks — 性能基准

net10.0 控制台程序，只测量、把结果写成 JSON，不以计时判定通过或失败（性能门槛只在窗口测量与性能报告中比较）。它引用启用 `VamSysTestSupport=true` 的存储与基础设施构建，不进入正式包；不带参数或参数不对时提示用法并以退出码 2 结束。结果的解读与前后对照见 [性能报告](../../docs/PERFORMANCE.md)。

| 命令 | 测量 |
|---|---|
| `--benchmark-core <JSON>` | 1 千／2 万／5 万行的规划、校验与位置索引 |
| `--benchmark-core-load <JSON>` | 同上，加工作区读取 |
| `--benchmark-review <JSON>` | 审阅计划构建 |
| `--benchmark-memory-load <JSON>` | 2 万／5 万行窗口基线工作区从数据库读取后的常驻托管内存、读取耗时、阻塞式完整 GC 耗时、与快照共用的草稿行数和字段名字符串实例数 |
| `--benchmark-journal <full｜journal> <目录>` | 5 万行工作区的保存开销（批量编辑、新增、解决冲突、只写头部、提交任务、无改动切换、保存连接设置） |
| `--benchmark-journal-jobs` | 任务写入的开销 |
| `--benchmark-storage <目录>` | 1／2 万行、有无撤销历史时任务与变基检查点的写入量和耗时 |
| `--benchmark-v3 <目录>` | v3 存储的检查点与变基 |
| `--benchmark-performance <目录>` | 判重索引与航线批准的请求次数和耗时 |

~~~powershell
dotnet run --project tests/VamSys.Benchmarks -c Release -- --benchmark-core artifacts/core-performance/current.json
dotnet run --project tests/VamSys.Benchmarks -c Release -- --benchmark-core-load artifacts/core-performance/current-load.json
dotnet run --project tests/VamSys.Benchmarks -c Release -- --benchmark-v3 artifacts/v3-rebase
~~~
