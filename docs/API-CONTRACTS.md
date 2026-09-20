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
