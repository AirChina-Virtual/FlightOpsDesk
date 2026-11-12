# VamSys.QaSeed — 人工查看的数据、打包检查与诊断回放

net10.0 控制台程序，只提供命令行入口，供 `scripts/open-datagrid-qa.ps1` 和打包脚本调用；不带参数或参数不对时提示用法并以退出码 2 结束。它引用启用 `VamSysTestSupport=true` 的存储与基础设施构建（输出在 artifacts/test-build），不进入正式包。[C# 窗口测试](../../tests/VamSys.UiTests/README.md)不经过这里，在进程内用 `DataUiFixture` 准备数据、直接读存储核对。

## 命令

| 命令 | 作用 |
|---|---|
| `--seed-data-ui <目录> <行数>` | 人工查看数据页用的合成航线 |
| `--verify-package <目录>`、`--verify-qa-package <目录>` | 打包检查（`PackageChecks`），`scripts/build.ps1 -Publish` 每次发布都会运行 |
| `--export-diagnostic-replay <JSON路径>` | 通过模拟 422/500 生成脱敏诊断与 SHA-256 |
| `--check-contract <仓库目录>` | 契约漂移检查（综合优化计划 4.3），见下 |
| `--verify-read-only <报告JSON路径>` | 对真实 vAMSYS 的只读验证（综合优化计划 5.1），见下 |

所有 `--seed-*` 入口在写入前都会拒绝已有文件的目录。

~~~powershell
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --verify-package artifacts/flightops-app
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --verify-qa-package artifacts/v3-qa
~~~

## 打包检查

`PackageChecks.Verify` 要求正式包和 QA 包都含有运行所需文件、不含开发目录与源文件、不含测试或工具程序集、文档链接有效；正式包不含调试符号、QA 入口（`CreateVerification` 等）、QA 钩子实现、`VAMSYS_QA_` 开关字符串、存储测试钩子和友元授权，QA 包则必须含有这些。检查本身的测试见 `IntegrationTests.Packaging.PackageChecksTests`。

编译服务不可用时，可先 `dotnet build tools/VamSys.QaSeed/VamSys.QaSeed.csproj -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nr:false`，再直接运行 `dotnet tools/VamSys.QaSeed/bin/Release/net10.0/VamSys.QaSeed.dll`。

## 契约漂移检查

`ContractDrift` 读取 `docs/contracts/manifest.json`，对每个来源（只接受 `https://vamsys.io`）发一次 GET，与固定副本比较。固定副本与清单中的 SHA-256 不一致时直接停止，不发请求。哈希不同时，排除 `description`、`summary`、`example(s)`、`default`、`title`（`properties` 下同名字段照常比较）并忽略键顺序后，列出版本变化、增删改的操作（逐项说明哪部分变了）和组件，以及自身未变但经 `$ref` 引用到变化组件的操作。抓到的副本保存在 `artifacts/contract-drift/<名称>-<UTC时间>.json` 供人工查看；固定副本和适配器嵌入的契约不会被替换。退出码 0 全部未变、1 有变化、2 无法检查（网络、哈希不符等）。不带任何账户信息，不需要登录。

~~~powershell
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --check-contract .
~~~

检查本身的测试见 `IntegrationTests.Packaging.ContractDriftTests`（用模拟来源，不连网）。

## 只读验证（5.1）

`ReadOnlyVerification` 一次完成 E4、E8、G7、E10 需要的真实请求，只发 GET 和取令牌的 POST；另有一层处理器在请求发出前拒绝其他任何方法。GET 走产品传输层（每秒 1 次的限流预算、只读重试）。

| 项 | 做法 |
|---|---|
| E10 | 分别以 scope `*`、不带 scope、空 scope 取令牌，各用 1 次 GET 看能否读取；只记录状态码和 OAuth 错误码 |
| E4 | 机场、机队、航路按每页 100 和 500 读全，航线另读 1000；比较 ID 集合是否一致、有无重复，记录每页实际条数和游标是否保留页大小。飞机读出各机队后，最大的机队再按 100 和 500 各读一遍 |
| E8 | 取一个机场的 ICAO、IATA 和一个机队名，分别用原值、改变大小写、部分值筛选，与完整列表对照 |
| G7 | 在飞机所属机队用注册号原值、改变大小写、部分值筛选，再在另一个机队用原值筛选 |

凭据从环境变量 `VAMSYS_CLIENT_ID`、`VAMSYS_CLIENT_SECRET` 读取，没有时在控制台提示输入（密钥不回显）。凭据和令牌不写入报告。报告含 ID、条数、所用的样本值和状态码。真实 vCCA 数据约 180 次请求，约 3 分半（2026-11-11）。每页记录 `meta` 的字段名和种类（不保存游标值）；游标认 `next_cursor_url`，没有时用 `next_page_url`。

~~~powershell
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --verify-read-only artifacts/readonly-check/report.json
~~~

检查本身的测试见 `IntegrationTests.Packaging.ReadOnlyVerificationTests`（模拟服务器）。
