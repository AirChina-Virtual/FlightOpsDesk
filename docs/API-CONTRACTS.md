# Operations API 契约登记（2026-09-21）

规范来源：[Operations](https://vamsys.io/api-docs/operations.json)、[Pilot](https://vamsys.io/api-docs/pilot.json)。原始 JSON 已固定在 `contracts/`，抓取时间、OpenAPI 3.1.0、API 3.0.0 与 SHA-256 见 `contracts/manifest.json`。适配器嵌入同一 Operations 规范，并校验所生成请求的属性、类型、必填项、枚举及已声明边界。

不再将“缺少完整 OpenAPI”作为全局禁用理由。下表的本地测试是程序契约／行为测试，**没有真实实例联调**。运行时按当前工作区和契约哈希记录操作回读结果；首次操作最多 3 条样本，人工确认不计为自动验证。

## 资源契约

| 资源 | 方法与路径 | 响应与重要语义 | 文档确认 | 本地测试 | 实例联调 |
|---|---|---|---|---|---|
| 机场 | GET/POST `/airports`；GET/PUT/DELETE `/airports/{airport_id}` | 创建用 `icao_iata`，后续按远端 ID；POST 裸对象，GET/PUT `data` 包装；DELETE 204 软删除 | 是 | 通过 | 未进行 |
| 机型 | GET/POST `/fleet`；GET/PUT/DELETE `/fleet/{fleet_id}` | 创建 name/code/type 后 PUT 容量等；POST 裸对象，单条 GET/PUT 包装；永久删除 | 是 | 通过 | 未进行 |
| 飞机 | GET/POST `/fleet/{fleet_id}/aircraft`；GET/PUT/DELETE `/fleet/{fleet_id}/aircraft/{aircraft_id}` | 遍历机型读取；创建 name/registration 后补充属性；单条 GET/PUT 包装；永久删除 | 是 | 通过 | 未进行 |
| 航路 | GET/POST `/routings`；GET/PUT/DELETE `/routings/{routing_id}` | 起降为机场 ID，更新不能改变；单条 GET/POST/PUT 裸对象；tag 需创建后 PUT；永久删除 | 是 | 通过 | 未进行 |
| 航线 | GET/POST `/routes`；GET/PUT/DELETE `/routes/{route_id}` | fleet_ids 为整数数组；UTC HH:MM:SS，日期 ISO 8601；起降不可更新；软删除 | 是，实验接口 | 通过 | 未进行 |

集合读取处理 `data` 数组与 `meta.next_cursor_url`，分页默认 15 条，不猜测更大页数上限。分页限制 HTTPS、同源及 Operations 路径，拒绝循环页。

## 认证与执行

POST `https://vamsys.io/oauth/token`，form-urlencoded：grant_type=client_credentials、client_id、client_secret、scope=*。授权范围由 Orwell 配置，应用不自动扩大服务器权限。缓存 token 按 expires_in 更新，不使用 Pilot refresh_token。

所有请求使用 Bearer 和 Accept: application/json。预算为进程全局共享的 60 次／分钟，保守覆盖未知 VA 及同一 VA 的多个工作区；429 遵守 Retry-After，剩余额度为零至少等待 60 秒，不假设 Reset 头单位。

读取临时错误退避重试；写入不自动重试。401/403 暂停任务。422／其他错误保留脱敏诊断；500、超时和中断写入保留未知结果。分步创建在 PUT 前保存 ID。新建前检查重复，修改前检查字段冲突，删除前读取相关资源进行引用保护。

## 明确禁用与未适配

| 行为 | 原因 | 状态 |
|---|---|---|
| PATCH `/aircraft/{aircraft_id}` 更换机型 | MoveAircraftFleetData 是无 properties 的空对象，无法得知目标字段 | 禁用，待官方补齐 |
| 已有航线 Fleet IDs 替换／追加／移除／清空 | 请求字段类型明确，但集合替换、空集合限制仍需核实 | 禁用，待实例语义验证 |
| 转换到／离开 Jumpseat | 文档说明清除机型与容器关联，处理顺序没有明确说明 | 本轮禁用 |
| 未确认清空 | nullable 声明不替代具体更新语义 | 禁用；机场 container_ids:[] 例外，已有明确说明 |
| Airport Pax/Cargo LF ID、Fleet Allowed Prefix IDs | CSV 与 API 对应关系／关联对象的部分更新方式未核实 | 保留 CSV，阻止这些字段在线提交 |
| SimBrief／复杂对象编辑 | 保留原始 JSON，当前表格未提供相应编辑器；部分响应 schema 与例子矛盾 | 不生成此类请求 |
| 软删除自动确认 | GET 可见性和最终停用状态缺少可依赖的统一判据 | 已接收后标未知，支持显式人工确认；不解锁批量删除 |

RouteData 中 fleet_ids/container_ids 声明 string 数组却有整数例子，部分对象属性声明数组却有对象例子。读取机型 ID 兼容 string／number 元素，保留完整原始 JSON；写入只按照请求 schema 生成整数数组。不会把请求中列出的“明确 prohibited”旧 SimBrief 字段生成为有效输入。

API 日期／时刻的原始精度保存在 RawApiJson；表格和 CSV 投影按原格式显示。修改分钟保留秒数，修改日期保留小数秒。清空和未提供严格区分，不将所有空单元格自动转换为 null。

## CSV 依据与 Pilot 边界

CSV 工作流继续依据官方 [Data operations](https://vamsys.co.uk/docs/data-operations) 下各资源导入导出规则，包括 ID／ICAO-IATA 标识和 `_delete`。CSV 删除规则不推导 API 删除方法。机场服务端允许引用未清除时软删除，应用采用更严格的本地／提交前引用保护；这属于应用约束。

Pilot API 是个人授权的 Authorization Code + PKCE，涉及 profile、booking、PIREP 等个人操作，不用于本轮 VA 配置管理。其规范仅归档，不创建 Pilot OAuth 入口。

## 后续实例验收

使用用户提供的 Operations 凭据，先验证五类资源分页和标识，再逐操作提交少量样本。检查字段、类型、返回 ID、分步创建、权限拒绝、远端冲突、软删除实际可见性与超时后的结果。只将实际回读通过的对应操作记录在工作区；文档缺口功能不因其他操作成功而自动解锁。


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

任务停止时允许单独更新 Operations Client ID／Secret；保留绑定的 AirlineId、接口地址、时刻配置和原任务。更新使旧客户端失效并要求重新连接；响应的 airline_id 必须匹配原 VA，没有可识别 VA 的数据时不宣告连接验证成功。未知写入仍只读恢复。

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

上述规则为客户端保护，不引入新端点、字段、删除语义或实例验证标记。软删除仍遵循现有人工核对边界；限流和未确认能力保持原状。
