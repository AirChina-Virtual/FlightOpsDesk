# Operations API 契约登记（2026-10-20）

规范来源：[Operations](https://vamsys.io/api-docs/operations.json)、[Pilot](https://vamsys.io/api-docs/pilot.json)。原始 JSON 已固定在 `contracts/`，抓取时间、OpenAPI 3.1.0、API 3.0.0 与 SHA-256 见 `contracts/manifest.json`。适配器嵌入同一 Operations 规范，并校验所生成请求的属性、类型、必填项、枚举及已声明边界。

Operations 固定为本轮 10 月 20 日证据中的快照，SHA-256 为 `1EC1A869FC3C6F4D70A7A6ECD50170D5D915551794E796F9F34D5AFB944D78C2`。与 9 月版本同为 API 3.0.0、173 个操作；排除说明、示例、默认值和标题后，35 个组件 schema 存在结构差异（含请求、查询和响应）。运行时不自动替换契约，新增字段仍受客户端白名单与独立兼容性策略限制。Pilot 保留原有归档。官方文档是否变化用 `VamSys.QaSeed --check-contract` 只读检查（见 [QaSeed 说明](../tools/VamSys.QaSeed/README.md#契约漂移检查)）；2026-11-10 检查时 Operations 仅 `DELETE /bookings/{booking_id}` 的两处说明文字不同，结构与固定副本一致。

已有独立真实 API 脚本测试；下表的本地测试指软件实际代码的模拟回归，不能推导软件真实写入或窗口验收。证据分级见 [能力验证矩阵](CAPABILITY-MATRIX.md)。已按用户确认开放固定契约下的独立实测范围，见 [字段与证据清单](VERIFIED-API-SCOPE.md)；这些请求不再受首次三条限制。未覆盖变体保留原有样本／兼容限制。外部基线不写工作区验证标记，只有软件实际成功回读才登记。

## 资源契约

| 资源 | 方法与路径 | 响应与重要语义 | 文档确认 | 本地测试 | 独立真实 API |
|---|---|---|---|---|---|
| 机场 | GET/POST `/airports`；GET/PUT/DELETE `/airports/{airport_id}` | 创建用 `icao_iata`，后续按远端 ID；POST 201 `data` 包装（2026-11-16 实测，此前文档记为裸对象），GET/PUT `data` 包装；DELETE 204 软删除 | 是 | 通过 | 外部脚本部分通过 |
| 机型 | GET/POST `/fleet`；GET/PUT/DELETE `/fleet/{fleet_id}` | 容量、parameter_ids 等已支持字段可随首次 POST；POST 201 `data` 包装（2026-11-16 实测，此前文档记为裸对象），单条 GET/PUT 包装；永久删除 | 是 | 通过 | 外部脚本部分通过 |
| 飞机 | GET/POST `/fleet/{fleet_id}/aircraft`；GET/PUT/DELETE `/fleet/{fleet_id}/aircraft/{aircraft_id}` | 遍历机型读取；容量等已支持字段可随 POST；POST 201 `data` 包装（2026-11-16 实测）；单条 GET/PUT 包装；永久删除 | 是 | 通过 | 外部脚本部分通过 |
| 航路 | GET/POST `/routings`；GET/PUT/DELETE `/routings/{routing_id}` | 起降为机场 ID，更新不能改变；单条 GET/POST/PUT 裸对象（POST 2026-11-16 实测仍为裸对象）；tag 可随 POST；永久删除 | 是 | 通过 | 按矩阵区分 |
| 航线 | GET/POST `/routes`；GET/PUT/DELETE `/routes/{route_id}` | RouteData 没有 `airline_id`，归属取当前连接的 VA；fleet_ids 为整数数组；UTC HH:MM:SS，日期按 `2025-01-01T00:00:00+00:00` 格式提交；callsign／flight_number 对所有类型必填；POST 201 `data` 包装（2026-11-16 实测）；cost_index 为空、AUTO 或 0–999；起降不可更新；软删除 | 是，实验接口 | 通过 | 自建 scheduled/jumpseat 样本通过 |

**POST 应答形状（2026-11-16 软件真实验收，从保存的任务记录读出）**：`POST /airports`、`POST /fleet`、`POST /fleet/{id}/aircraft`、`POST /routes` 回 `201 {"data":{"id":32329,...}}`，`POST /routings` 仍回裸对象 `{"id":56386,...}`。当时的客户端只在顶层找 `id`，包装应答中的 ID 没有读到：这几项的 POST 步骤停在“已发出”、没有记下 ID，结果未知，直到本轮结束的自动核对按候选找回 ID（来源 Automatic）；依赖同一任务中新机队的飞机和 scheduled 航线因此被跳过，等用户点继续。2026-11-15 起两种形状都能读出 ID，模拟服务器（`FakeRemote`）也按上面的实测形状应答。

集合读取处理 `data` 数组与 `meta.next_cursor_url`，没有时用 `meta.next_page_url`。2026-11-11 只读验证（综合优化计划 5.1）：真实响应只有 `next_page_url`／`next_cursor`，没有契约所写的 `next_cursor_url`；`page[size]` 超过 100 时服务器静默按 100 返回（`meta.per_page=100`）。每页请求 `page[size]=100`（文档默认 15 条，未写上限，实际上限 100）；首页如果因此返回 400／422，改用服务器默认页长继续读取。分页限制 HTTPS、同源及 Operations 路径，拒绝循环页。

除航线外，其余四类数据缺少 `airline_id` 时仍会拒绝，继续用于识别错误的 VA。航线数据本身不能证明连接到了哪个 VA，因此连接验证仍要读取机型或机场。

## 认证与执行

POST `https://vamsys.io/oauth/token`，form-urlencoded：grant_type=client_credentials、client_id、client_secret、scope=*。授权范围由 Orwell 配置，应用不自动扩大服务器权限。缓存 token 按 expires_in 更新，不使用 Pilot refresh_token。

所有请求使用 Bearer 和 Accept: application/json。预算为进程全局共享的 60 次／分钟，保守覆盖未知 VA 及同一 VA 的多个工作区；429 遵守 Retry-After，剩余额度为零至少等待 60 秒，不假设 Reset 头单位。

读取临时错误退避重试；写入不自动重试，唯一例外见下（E7，D5）。401/403 暂停任务。写入收到 429，或请求根本没有发出（等待限流额度或获取令牌时失败，认证失败和取消除外）时，服务器没有处理该请求：本轮其余条目处理完后，按 Retry-After 等待，重新做写前检查后再发一次，每条每轮最多一次；仍不成功则放回待执行，继续任务即可重试；已返回 ID 或已接受的写入仍按未知处理。422／其他错误保留脱敏诊断；500、超时和中断写入保留未知结果。POST 返回后先保存 `id`，再解析完整响应；`id` 从裸对象或 `data` 包装中读取，两种都认，都没有可用 `id` 时按结果未知处理、只靠读取找回；分步创建在 PUT 前保存 ID。新建前检查重复，修改前检查字段冲突，删除前读取相关资源进行引用保护。

## 明确禁用与未适配

| 行为 | 原因 | 状态 |
|---|---|---|
| PATCH `/aircraft/{aircraft_id}` 跨机队移动 | 独立真实请求及目标父路径回读通过；只发整数 fleet_id，独立提交并核对目标归属 | 已开放；独立能力键，Unknown 只 GET |
| 已有 scheduled 航线 Fleet IDs 替换／追加／移除 | 独立真实样本 A→AB→B→A 已通过；软件预检批准关系并完整回读集合 | 已按实测范围开放；独立能力键 |
| 空航线机队集合、jumpseat 集合更新 | 不扩大已验证范围 | 继续禁用 |
| 转换到／离开 Jumpseat | 文档说明清除机型与容器关联，处理顺序没有明确说明 | 本轮禁用 |
| 未确认清空 | nullable 声明不替代具体更新语义 | 禁用；机场 container_ids:[] 例外，已有明确说明 |
| Airport Pax/Cargo LF ID、Fleet Allowed Prefix IDs | CSV 与 API 对应关系／关联对象的部分更新方式未核实 | 保留 CSV，阻止这些字段在线提交 |
| SimBrief／复杂对象编辑 | 保留原始 JSON，当前表格未提供相应编辑器；部分响应 schema 与例子矛盾 | 不生成此类请求 |
| 飞机顶层 weight_unit、机队单位换算 | 飞机显式单位 PUT 可复现 500；机队 lbs 输入读回不换算 | kg 整数原样提交；均省略 weight_unit |
| 软删除自动核对 | 限机场和已测 scheduled／jumpseat；要求原 DELETE 204、对应步骤和同 VA 同 ID 的后续 GET 404 | 已开放“API 已核对不可见”；不宣称物理删除，缺证据仍未知 |

RouteData 中 fleet_ids/container_ids 声明 string 数组却有整数例子，部分对象属性声明数组却有对象例子。读取机型 ID 兼容 string／number 元素，保留完整原始 JSON；写入只按照请求 schema 生成整数数组。不会把请求中列出的“明确 prohibited”旧 SimBrief 字段生成为有效输入。

API 日期／时刻的原始精度保存在 RawApiJson；表格和 CSV 投影按原格式显示。修改分钟保留秒数，修改日期保留小数秒。清空和未提供严格区分，不将所有空单元格自动转换为 null。

## CSV 依据与 Pilot 边界

CSV 工作流继续依据官方 [Data operations](https://vamsys.co.uk/docs/data-operations) 下各资源导入导出规则，包括 ID／ICAO-IATA 标识和 `_delete`。CSV 删除规则不推导 API 删除方法。机场服务端允许引用未清除时软删除，应用采用更严格的本地／提交前引用保护；这属于应用约束。

Pilot API 是个人授权的 Authorization Code + PKCE，涉及 profile、booking、PIREP 等个人操作，不用于本轮 VA 配置管理。其规范仅归档，不创建 Pilot OAuth 入口。

## 后续实例验收

本轮实施的真实服务回归仅执行 GET（OAuth 取令牌例外），不自动执行新的写入验收。以后软件真实写入验收须另行授权。只将软件实际回读通过的对应操作记录在工作区；其他能力的成功不自动解锁新能力。

## 10 月兼容性处理

- 货物容量只接收、显示并提交 kg 的非负整数；CSV 原列名、未知列与字节格式规则保留，离线小数不自动取整。在线审阅明确阻止小数、负值和溢出。空值仍表示省略／继承，未开放清空。
- 新在线机队选择一个 approved、unrestricted 参数，以字符串数组 `parameter_ids` 提交。参数查询只有只读模型，不新增资源 CRUD；旧 `Allowed Prefix IDs` 不自动映射，既有机队改绑／解绑禁用。旧任务缺少新选择要求标志时保留保存时的语义。
- scheduled 机队集合使用 `Routes:Update:FleetIds:Scheduled`，不继承旧 `Routes:Update` 标记；整数请求与 string/number 响应按规范化集合完整核对，拒绝重复、缺少或新增成员。参数与机队批准信息只在当前连接、当前执行会话缓存；机队变化、读取失败或相关 422 失效。
- 非 scheduled 的同类型机队集合修改（cargo、charter、training、vfr、repositioning，类型不变、集合非空）已开放，每种类型用自己的能力键 `Routes:Update:FleetIds:<Type>`（如 `Routes:Update:FleetIds:Cargo`），不借用 scheduled 的验证结果，按每类前三条样本执行、回读按集合完整核对后才继续，前缀预检照常运行。尚无真实写入证据。改类型的同时改机队、jumpseat 的机队修改仍阻止。
- 已有 jumpseat 允许呼号／班号为空并显示“服务端未保留”，这两个字段在线只读。创建仍提交合法必填值，只有这两个字段的显式 JSON null 可作为回读例外。机场、类型、远端 ID 与其他持久字段仍严格比较；缺字段或非空差异不豁免。
- **各航线类型的服务器处理（2026-11-12 核查）**，来源：固定契约、vCCA 快照（3,580 条航线的原始 JSON，只读分析）、14217362 的写入与回读、独立脚本的已验证范围：

  | 类型 | 证据 | 机队 | 呼号／班号 | 其他字段 | 客户端规则 |
  |---|---|---|---|---|---|
  | scheduled | 快照 3,181 条；自建样本 CRUD | 保存，必填 | 保存，需前缀批准 | 时刻、Routing、备注、Service Days、日期按值保存 | 机队必填；前缀预检 |
  | cargo | 快照 394 条；无软件写入 | 保存 | 保存 | 同上 | 同 scheduled；同类型机队修改按 `Routes:Update:FleetIds:Cargo` 小样本执行 |
  | repositioning | 快照 3 条；2026-11-06 软件新建 2 条 | 保存 | 保存，需前缀批准（422 证据） | 同上 | 同 scheduled |
  | training | 快照 2 条；无写入 | 保存 | 保存 | 同上 | 同 scheduled |
  | charter、vfr | 无（vCCA 没有这类航线） | 契约：必填 | 契约：必填 | 未知 | 同 scheduled |
  | jumpseat | 独立脚本 CRUD；2026-11-12 软件新建 14217362 | **不保存**（发送的 `fleet_ids` 回读为 null） | 契约必填，但**不保存**（回读为 null） | 时刻、Routing、备注、内部备注、Cost Index、Service Days、隐藏、日期按值保存（独立脚本）；`container_ids` 契约写明忽略；未填开始日期时服务器填当天 | 机队必须为空（校验、规划、机队选择三处拦截）；呼号／班号照填，回读 null 视为一致；已有 jumpseat 的呼号／班号只读；不能改入或改出 jumpseat（契约：会清空机队和集装箱） |

  只有 jumpseat 有“发送了但不保存”的字段。所有类型的 `flight_length`、`flight_distance` 由服务器计算，客户端不发送。
- jumpseat 不保存机队：2026-11-12 真实验收中带 `fleet_ids: [32227]` 新建的 jumpseat（航线 14217362）回读为 `fleet_ids: null`。此后 jumpseat 填了机队会在校验中标出、规划时拒绝、机队选择中拒绝；已经带机队发出的旧任务，回读到 null 视为一致。
- jumpseat 候选按机场对、类型、起降时刻与有效日期比较。无 ID 的人工候选须不在提交前候选 ID 中，持久字段一致且创建时间在持久化请求窗口内（±60 秒）；缺证据保持 Unknown，不能重发。
- `updated_at` 不作为业务版本号。预检诊断分别保存时间戳和业务字段差异；仅元数据变化不制造冲突。不自动扫描 booking／PIREP，不将既有时间相关性写成确定因果。
- 诊断保留 HTTP、阶段、本地时间、服务器 Date、完整字段错误、error_id 和可用追踪头。先脱敏再按 1 MiB 限制正文，并保存截断标志和完整脱敏正文哈希；UI 摘要可缩短。OAuth 正文、凭据和 Cookie 不导出。原 Diagnostic 与新可选 JSON 字段兼容，数据库仍为 v4。
- 新建优先使用单次 POST；旧任务恢复保持原步骤、期望值和检查点顺序，不补发或重排旧写入。Unknown 的 GET 读回旧值仍不允许重发。


## 审核修复后的本地核对规则（2026-09-21）

- 结果未知和中断写入只执行读取核对；核对发生认证、限流、网络或解析错误时继续保留 Unknown，不能据此重新发送写入。旧任务已保存的创建完成／写入接受标志同样作为只读恢复依据。
- 恢复机场创建时，手工提供的远端 ID 仅用于定位；还必须匹配响应中的 ICAO／IATA 及预期属性。名称相同不构成身份相同。
- 航线／航路的起降关联按当前 VA 的远端机场 ID 比较；写入侧 ICAO、IATA 和远端 ID 使用同一解析规则。新增重复检查复用相同身份规则，不以表格显示值判断是否相同。
- 这些是客户端保护措施，已通过本地回归测试；不增加官方契约声明或实例验证状态。查询与创建之间仍可能有远端并发变更，服务端拒绝需按现有错误流程处理。


## 第二轮审核修复：请求期望与恢复候选

写请求与回读期望共用适配器生成的类型化请求步骤。只验证实际提交的 JSON 属性与资源身份：省略值不与服务器默认值比较，显式零、false、空集合不等同省略；布尔和数字按 JSON 类型比较，关联 ID 数组兼容已归档响应契约中的字符串／数字表示，日期保持已有精度要求。

预检、写请求、写后回读、未知恢复使用不同执行阶段。预检暂时性查询失败允许重试；可能已写入及恢复核对失败保持未知，不降级为可重新写入。

人工候选 ID 单独保存并保留提议历史，只有回读匹配后绑定 RemoteId 和资源身份。未确认候选可在线更正并核对，也可离线更正保存后再连接核对；创建响应已确认的身份禁止人工替换。以上均为本地保护及兼容行为，不代表新增实例验证结果。


## 第三轮审核修复：凭据与索引生命周期

任务停止时允许单独更新 Operations Client ID／Secret；保留绑定的 AirlineId、接口地址、时刻配置和原任务。更新使旧客户端失效并要求重新连接；响应的 airline_id 必须匹配原 VA（航线响应不含该字段），没有可识别 VA 的数据时不宣告连接验证成功。未知写入仍只读恢复。

完整刷新使用独立临时适配器及身份索引。所有资源读取、三方合并和持久化成功后才替换正式快照及索引；读取、取消、合并或保存失败保留原状态。确认删除后淘汰相应资源缓存。上述行为均属客户端生命周期保护，本地测试通过不增加真实实例验证状态。


## 凭据保存一致性补充（第四轮）

普通设置与仅更新凭据共用同一事务服务，Client ID 统一 trim，Secret 按不透明字符串原样保存。普通保存可保留同一归一化 Client ID 的旧 Secret；专用轮换入口要求新 Secret。旧带空格的凭据可通过普通保存修正。保存失败不更新工作区内存或替换会话；暂停任务的配置保护和同 VA 重连规则保持不变。


## 第五轮优化：数值筛选、完整索引与计时（2026-09-22）

采用已归档 GET 筛选字段：routes 的 departure_id / arrival_id / fleet_id，以及 routings 的 departure_airport_id / arrival_airport_id。创建使用机场对交集；机场删除分别查询出发、到达后取并集。飞机删除依赖限定机型路径，飞机创建判重仍跨全部机型。

机场代码、机型名称、注册号和航班号文本筛选的大小写规则未完成实例验证，本轮不启用，相关资源使用执行级完整候选索引或机场对候选内比较。所有候选仍分页、检查 VA 并在本地精确核对，不将筛选结果当成远端事务或唯一性保证。

查询失败保留预检失败分类，不自动退回全量扫描或切换模拟服务。共享限流与令牌计时可注入 TimeProvider，生产默认系统时钟和原有保守共享池；文本过滤、页大小和写入重试策略不扩大。详情见 PERFORMANCE.md。


## 执行级索引生命周期修复（2026-09-22）

判重签名保持既有客户端规则；机场 ICAO/IATA 从响应原始字段建立双别名索引，机型名称、飞机注册号保持大小写不敏感，航线/航路仍按机场远端 ID 与既有文本字段比较。不同 ID 的相同签名保留为集合，不使用单值覆盖。

同批创建、修改及永久删除只有完成原有回读核对后才更新确认索引。未知修改保留前后身份，未知删除保留原身份；机场创建尚未核对成功时暂停后续机场创建。受保护的新记录保持 Pending，未知记录仍保持 Unknown。输入候选 ID、空列表或恢复失败均不视为成功。恢复重建不确定预留，并重新读取确认数据；缺少原始 API 快照的历史成功记录不参与初始化，不重发其写入。

上述规则为客户端保护。现已另按独立实测证据开放软删除 204／404 核对和飞机 fleet_id 移动，范围见前文；不扩大到物理删除判定、未验证类型转换或复杂字段。共享限流、检查点和未知写入不重发保持原状。
