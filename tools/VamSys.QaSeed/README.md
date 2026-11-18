# VamSys.QaSeed — 人工查看的数据、打包检查与诊断回放

net10.0 控制台程序，只提供命令行入口，供 `scripts/open-datagrid-qa.ps1` 和打包脚本调用；不带参数或参数不对时提示用法并以退出码 2 结束。它引用启用 `VamSysTestSupport=true` 的存储与基础设施构建（输出在 artifacts/test-build），不进入正式包。[C# 窗口测试](../../tests/VamSys.UiTests/README.md)不经过这里，在进程内用 `DataUiFixture` 准备数据、直接读存储核对。

## 命令

| 命令 | 作用 |
|---|---|
| `--seed-data-ui <目录> <行数>` | 人工查看数据页用的合成航线 |
| `--verify-package <目录>`、`--verify-qa-package <目录>` | 打包检查（`PackageChecks`），`scripts/build.ps1 -Publish` 每次发布都会运行 |
| `--export-diagnostic-replay <JSON路径>` | 通过模拟 422/500 生成脱敏诊断与 SHA-256 |
| `--check-contract <仓库目录>` | 契约漂移检查（综合优化计划 4.3），见下 |
| `--compare-performance --baseline <路径> --current <路径> [--limit 10]` | 与 R0 基线的性能比对（I14），只读已有测量结果，见 [性能报告](../../docs/PERFORMANCE.md#与-r0-基线自动比对i14) |
| `--verify-read-only <报告JSON路径>` | 对真实 vAMSYS 的只读验证（综合优化计划 5.1），见下 |
| `--write-evidence <报告JSON路径> [--dry-run] [--max-requests N] [--only aircraft-internal-remarks]` | 对真实 vAMSYS 的写入取证（综合优化计划 6.1 G4、6.2 G8；`--only` 只跑一项小探测），只处理本轮自建的记录，见下 |

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

## 真实写入取证（6.1、6.2）

`WriteEvidence` 由用户手动对真实 vCCA 运行一次，取得两项证据：**G4** 服务器会不会规范化写入的值（首尾空格、大小写、数字与字符串、日期与时刻格式、数组顺序与重复等），**G8** cargo、charter、training、vfr、repositioning 航线删除后是 404 还是仍能读到。结果只写进报告，产品的比对和删除核对不变（改动属于 6.3）。

**流程。** ① 只读发现（只发 GET）：读航司参数、机队列表和全部航线，选一条已有的 scheduled 航线作模板，只读引用它的机队、两个机场和对应的航司参数（呼号／班号前缀），并挑出没有航线使用过的班号（从 9999 往下）；已有带 `FODESK-WE` 标记的航线或机队（以前的运行留下）只在计划里提示，不处理。② 打印完整计划：要新建的 9 条记录及其请求体、41 个探测值、每类请求次数、预计请求数和耗时。③ 输入确认词 `WRITE` 后才写入；其他任何输入都取消，不发写请求。④ G4：新建机队（对 Phoenix 隐藏，关联模板参数）、其下一架飞机、一条航路、一条 scheduled 航线，各回读一次；再逐个探测：每次 PUT 一个字段的一个值，随后 GET，记录发送值、回读值、修改前的值、是否严格相等、差异类型（`trim`、`case`、`number-string`、`date-format`、`array-order`、`empty-to-null`、`not-applied` 等，见 `Normalisation`；422 等拒绝记为 `rejected`）。⑤ G8：五种类型各新建一条航线，回读，DELETE，再 GET，记录状态码和读到的内容。⑥ 清理：按航线、航路、飞机、机队的顺序删除仍存在的自建记录，每条删除后再 GET 一次。

**安全。** 新建的航线全部隐藏、60 天后才生效、带 `FODESK-WE <UTC时间>` 标记（备注和内部备注）；机队名称、飞机名称与内部备注、航路备注也带标记。一层处理器在请求发出前拒绝：确认前的任何写请求；不是计划内集合的 POST；不是本轮新建应答所给 ID 的 PUT／DELETE；超过上限的请求（默认 200 次，其中 18 次留给清理）。读请求走产品传输层（每秒最多 1 次、遵守 429／Retry-After）；写入收到 429 时按 Retry-After 等待后最多再发 2 次（服务器未处理）。新建没有得到可用应答（超时、5xx、应答无 ID）或请求未发出、401／403 时立即停止并清理；新建被 422 等拒绝只跳过这一项（飞机依赖机队）。Ctrl+C 在当前请求完成后停止并清理，再按一次放弃清理。清理不掉的记录（含“POST 已发出但没有取得 ID”的）在报告 `leftovers` 和控制台用 `!!` 醒目列出 ID、路径和标记，退出码 4。

凭据读取与 `--verify-read-only` 相同（环境变量 `VAMSYS_CLIENT_ID`、`VAMSYS_CLIENT_SECRET`，否则在控制台输入，密钥不回显），凭据和令牌不写入报告。报告含发现结果、计划、每条记录的状态、`G4_createReadback`、`G4_probes`、`G4_summary`、`G8_softDelete`、`cleanup`、`leftovers`，`requests`（确认后的每个请求：方法、路径、状态码、发送的请求体、应答或错误）和 `wire`（实际发出的每个请求，含发现阶段的读取、重试和取令牌：方法、路径、状态码，不含请求体和游标值）。

退出码：0 完成且全部清理（`--dry-run` 也是 0）；1 未确认；2 无法开始（发现失败、超过上限、参数错误）；3 中途停止但已全部清理；4 有记录没有清理掉。

**预计请求数（vCCA）。** 服务器每页最多返回 100 条（5.1 结果），读约 3,600 条航线约 36 次，发现阶段共约 40 次；确认后 POST 9、PUT 41、DELETE 9、GET 59，共 118 次；全程约 160 次，按 5.1 实测每次约 1.1 秒，约 3 分钟。实际数以控制台打印的计划为准。

~~~powershell
# 先只看计划（只发 GET）
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --write-evidence artifacts/write-evidence/plan.json --dry-run
# 真实写入（打印计划后输入 WRITE 确认）
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --write-evidence artifacts/write-evidence/report.json
~~~

工具本身的测试见 `IntegrationTests.Packaging.WriteEvidenceTests`（模拟服务器）：正常流程（含一次 429、服务器规范化的分类、某类航线删除后仍可读）、确认被拒、dry-run、中途传输失败后清理、Ctrl+C 后清理、只写自建 ID、清理失败时的报告，以及差异类型的分类。

### 只测飞机 internal_remarks（`--only aircraft-internal-remarks`）

2026-11-18 的真实运行中，新建飞机时随 POST 发送的 `internal_remarks` 在 201 应答和随后的 GET 里都是 null。这项小探测只查一件事：PUT 能不能保存这个字段。只读发现只读航司参数和机队列表（不读航线），然后新建 1 个机队（与 G4 相同，对 Phoenix 隐藏）和其下 1 架飞机（POST 带 `internal_remarks`），回读；再依次 PUT 只含 `internal_remarks` 的请求体：两个不同的非空值、空字符串，每次后 GET 回读。新建后和每次 PUT 后另读一次机队下的飞机列表，单条 GET 和列表都区分“字段不存在”与 null。最后先删飞机、后删机队。不读、不写航线和航路。守卫、确认词 `WRITE`、`--dry-run`、Ctrl+C 清理、请求上限和退出码与完整运行相同。

控制台结尾逐步列出发送值、单条 GET 和列表读到的值，并给出结论（PUT 后回读与发送的非空值一致即“PUT 能保存”）；报告的 `aircraftInternalRemarks` 含 `postSaved`、`putSaved`、`conclusion` 和每一步的 `steps`。

**预计请求数（vCCA）。** 只读发现 3 次（取令牌、航司参数、机队各 1 次，机队超过 100 个时多几页）；确认后 POST 2、PUT 3、GET 11（新建回读 2、PUT 后回读 3、飞机列表 4、清理后回读 2）、DELETE 2，共 18 次；全程约 21 次。按 2026-11-18 实测每次约 1.5 秒，约半分钟。

~~~powershell
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --write-evidence artifacts/write-evidence/aircraft-remarks-plan.json --only aircraft-internal-remarks --dry-run
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --write-evidence artifacts/write-evidence/aircraft-remarks.json --only aircraft-internal-remarks
~~~

测试见 `WriteEvidenceTests` 中 `AircraftRemarksOnly*`：服务器保存与不保存该字段两种情形（含列表不返回该字段）、只跑这一项时不读写航线和航路、dry-run 只读参数和机队；未知的 `--only` 值在发出任何请求前被拒绝。
