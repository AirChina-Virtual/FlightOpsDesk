# 独立真实 API 证据与本版开放范围

本版按用户确认，开放机场、机队、飞机、航路、航线五类资源中已由隔离记录完成真实请求和回读的范围，包括飞机跨机队移动、机场和航线的删除核对。这里的依据是此前独立脚本测试，不表示本轮进行了软件真实写入或窗口验收。

基线标识为 `vcca-isolated-20261020`，只适用于固定契约 SHA-256 `1EC1A869FC3C6F4D70A7A6ECD50170D5D915551794E796F9F34D5AFB944D78C2`。程序不下载证据或自动替换契约，也不把该基线写入工作区的 `VerifiedOperations`。

## 本版解除首次样本限制的请求范围

下表只列客户端已有的编辑字段；独立脚本中的其他字段不会因此自动成为客户端编辑能力。所有请求仍必须通过实际适配器校验、VA 归属检查、引用／批准预检和提交确认。

| 资源 | 创建 | 修改与额外操作 | 删除 |
|---|---|---|---|
| 机场 | ICAO/IATA、Name | Name | 原请求 204 与同 ID 详情 404 完整对应后确认 API 不可见 |
| 机队 | Name、Type Code、Type、Max Passengers、Max Freight、Hide in Phoenix、Parameter IDs；已测类型 pax-cargo／cargo | Name、Max Passengers、Max Freight | 详情不可见后按既有永久删除规则核对 |
| 飞机 | Fleet ID、Name、Registration、Passengers、Freight、Internal Remarks | Name、Freight；另支持单独只改 Fleet ID 的跨机队移动 | 按既有永久删除规则核对 |
| 航路 | 起降机场、Route String、Remarks | Remarks | 按既有永久删除规则核对 |
| scheduled 航线 | 类型、呼号、班号、起降机场、非空 Fleet IDs、时刻、Routing、隐藏、有效日期、备注、内部备注、Service Days | Remarks、Cost Index、起降时刻；非空机队集合替换／追加／移除 | 完整 204／404 证据后确认 API 不可见 |
| jumpseat 航线 | 类型、呼号、班号、起降机场、时刻、Routing、隐藏、有效日期、备注、内部备注、Cost Index、Service Days | Remarks、Cost Index、起降时刻 | 完整 204／404 证据后确认 API 不可见 |

未覆盖的字段或变体仍使用原有样本和兼容限制。例如，Tags 随创建、scheduled 创建时的 Cost Index、jumpseat 创建时的机队关联没有进入独立证据豁免。新的在线机队仍要求选择一个已批准、未受限的航司参数；既有机队参数改绑不开放。

## 飞机跨机队移动

移动使用 `PATCH /aircraft/{id}`，仅提交整数 `fleet_id`。它必须单独提交，目标机队属于当前 VA、存在且未标记本地删除。执行前从原机队路径核对飞机，再读取目标机队；写后从 `GET /fleet/{target}/aircraft/{id}` 核对 ID、归属和 `fleet_id`。成功后在检查点事务中更新飞机身份、快照和草稿。

500、超时、中断或读回不匹配均保留 Unknown；恢复只读取目标路径，不重发 PATCH。旧任务的保存步骤和期望值继续原样校验。独立测试证明过移入目标及移回原机队，并未证明移动后旧父路径必然立即 404，软件不依赖这个假设。

## 软删除与历史任务的只读核对

机场和所有类型的航线（scheduled、jumpseat；2026-11-18 起另有 cargo、charter、training、vfr、repositioning，只限删除）只在以下证据同时成立时自动成功：原始 DELETE 返回 204、对应唯一步骤已保存为 ResponseReceived、VA 与远端 ID 一致，随后同一路径 GET 返回 404，且请求时间对应。任务显示“API 已核对不可见”，不宣称物理删除。

任务中心的“API 只读核对删除”仅处理 Unknown 或人工确认的软删除项，只发 GET；同任务内的 Pending 项不执行。原先人工确认的任务可以补充 API 证据，并保留 `WasManuallyConfirmed` 来源。缺原始回执、GET 200、权限错误或临时读取失败时不改成成功，也不重发 DELETE。人工确认和 API 核对均在持久化成功后才更新内存。

升级、连接和解锁不会自动运行已有任务，也不会自动改写人工确认记录。工作区能力标记只由软件本次实际回读成功登记；外部基线只改变审阅放行范围。

## 2026-11-18 写入取证

用户于 2026-11-18 00:16–00:20 UTC 用 `VamSys.QaSeed --write-evidence` 对真实 vCCA 运行一次（运行标记 `FODESK-WE 20261118T001624Z`，153 次请求，自建 9 条记录全部清理），报告为 `artifacts/write-evidence/report.json`。

**回读等价规则（G4）。** 写后回读只对下列字段接受对应改写，规则登记在 `ServerNormalisation`，每条注明报告条目；其余字段和其余改写仍严格比对，不一致保持“结果未知”。

| 改写 | 字段 | 报告条目 |
|---|---|---|
| 去首尾空格 | 机队 name、飞机 name、registration、航路 remarks、航线 remarks | `trim-padding` |
| 转大写 | 机队 code、飞机 registration、selcal、hexcode、航线 callsign、flight_number | `lowercase` |
| 数字字符串→数字 | 机队 max_pax、max_cargo，飞机 passengers、cargo，航线 altitude | `number-as-string` |
| 空字符串→null | 飞机 fin_number，航路 remarks，航线 remarks | `empty-string` |
| 星期名小写（并按周一到周日排序） | 航路 days_of_operation，航线 service_days | `case`、`array-order` |
| `HH:MM`→`HH:MM:00` | 航线 departure_time | `time-without-seconds` |
| 只写日期→当日 00:00 UTC | 航线 start_date | `date-only` |
| 丢弃毫秒 | 航线 end_date | `date-fraction` |

以下情况本来就相等，不需要规则：`130.0`→`130`；数组中的字符串 ID→数字（航线 fleet_ids、机队 parameter_ids，原有按 ID 集合比对）；`Z`→`+00:00`；cost_index 的 `auto`、`050` 原样保存；tag 不排序、不去重；机队 name 不改大小写、内部连续空格保留。

不放宽的：
- 带非 UTC 偏移的日期，服务器丢掉偏移、保留墙上时间（`08:00+08:00` 存成 `08:00+00:00`），这是另一个时刻。程序发送日期一律为 `+00:00`（本地时间的航线不能在线提交），不会遇到；遇到时仍判不一致。
- 被 422 拒收的：`H:MM:SS` 时间、数字形式的 cost_index（只接受字符串）、去空格或小写的航路 route string。
- 只测了一个字段的改写不推广到同类字段：arrival_time 没有 `HH:MM` 规则、end_date 没有“只写日期”规则、start_date 没有“丢弃毫秒”规则；程序本来也不会发出这些形式。
- **飞机 Internal Remarks（已采用“新建后补一次 PUT”，2026-11-17）。** 新建时随 POST 发送的 `internal_remarks` 不保存：201 应答、单条 GET 和机队下的飞机列表都是 null。补充取证（`artifacts/write-evidence/aircraft-remarks.json`，运行标记 `FODESK-WE 20261118T005911Z`）证明只含 `internal_remarks` 的 PUT 返回 200、两处回读与发送值一致、可多次修改，PUT 空字符串回读为 null。所以在线新建飞机且填写了 Internal Remarks 时，程序先 POST（不带这一列），拿到新飞机 ID 后再对它 PUT 一次只含 `internal_remarks` 的请求，然后回读核对全部值；没填写时只发 POST。PUT 只在 POST 应答给出 ID 之后发出；POST 应答丢失时不发 PUT（找回的候选 ID 也不发）；PUT 收到 429 或未发出时，“继续”先回读核对 POST 写入的值，再只补发 PUT，不会再次新建；PUT 超时、5xx 或应答丢失时不重发，按回读结果判定。飞机 `internal_remarks` 登记了“空字符串→null”的回读规则。

**软删除（G8）。** cargo、charter、training、vfr、repositioning 航线各新建一条后 DELETE 均 204，随后 GET 均 404，与 scheduled、jumpseat 相同。这些类型的删除现在按同一套 204＋404 证据自动核对“API 已核对不可见”，审阅时不再受首次样本限制；它们的新建和修改没有新证据，仍保留样本限制。

**前缀检查（2.1）。** 本次航线都用合法前缀 CCA／CA，没有测被拒情况；cargo、charter、training、vfr 的前缀检查仍是按决定 D11 阻止，尚无服务端证据。

## 证据索引与未开放边界

- `flightops-api-complete/request-audit.jsonl` 与 `safe_api_test.py`：五类 CRUD 和飞机移动。移动的两次 PATCH／新父路径 GET 见审计 138–143 行，最小载荷见脚本 489–502 行。
- `flightops-api-complete/route-followup`：jumpseat CRUD 及 204／404 删除回读。呼号、班号列表筛选的失败不列为已验证能力。
- `flightops-scheduled-9677`：新机队参数关联、scheduled 创建、Remarks／Cost Index／带秒数时刻更新、机队集合 A→AB→B→A，以及 204／404 清理闭环。参数 ID 只是该次测试样本，程序不硬编码它。
- `flightops-weight-isolation`：省略单位的容量写入成功；显式单位和 SimBrief 的 500 仍是失败证据。
- `artifacts/write-evidence/report.json`（运行标记 `FODESK-WE 20261118T001624Z`）：G4 回读规范化 41 项探测和 103 项新建回读，G8 五种航线类型的删除 204／404。见上节。

这些材料位于本机历史证据目录，不随正式包分发。公开源码中的范围实现为 `IndependentApiEvidence`；回归直接调用实际适配器、执行器、存储和只读核对服务。173 个操作中 96 次成功的旧汇总不能泛化成完整 API 开放。

继续禁用 weight_unit／lbs 换算、SimBrief 编辑、空航线机队集合、jumpseat 集合修改、进入或离开 jumpseat 的类型转换、既有机队参数改绑／解绑和未验证清空。新增真实写入验收不是本版自动步骤。

[能力矩阵](CAPABILITY-MATRIX.md) · [API 契约](API-CONTRACTS.md) · [测试报告](TEST-REPORT.md)
