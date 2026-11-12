# 软件真实流程验收（综合优化计划 4.2）

在正式程序中，对真实 vCCA 上**本次新建的隔离记录**完成五类资源的新建、修改、删除和飞机移动，最后全部清理。只动名称带 `QA42` 的记录，不改任何已有数据。完成后由开发者只读整理本地任务记录，登记到 [能力矩阵](CAPABILITY-MATRIX.md)。

## 准备

1. 用正式包（`artifacts/flightops-app`）打开 vCCA 工作区，在“连接与设置”中由你本人输入凭据并测试连接。
2. 数据页“数据”菜单 →“从 API 刷新”，确认没有待审阅冲突、没有其他待提交改动（审阅页为空）。如有其他改动，先处理掉或换一个新工作区，避免混进本次提交。
3. 选一个 vCCA **还没有**的机场 ICAO（在机场页搜索确认不存在），下文记为 `XXXX`。
4. 选一个能用 CCA／CA 前缀的已批准参数 ID（新建机队时在“Parameter IDs”的选择框里选），下文记为 `P`。
5. 选两个已有机场作为航路和航线的起降，下文用 ZSPD、ZSSS 举例。

每一轮：在数据页填好 → 审阅页检查 →“审阅并提交” → 任务中心等任务结束。记下每一轮任务的结束状态，以及任何不是“成功”的条目和它的提示文字（截图即可）。**某一轮有条目失败或结果未知时先停下，不要进入下一轮，把情况告诉开发者。**

## 第 1 轮：新建（一次提交）

| 资源 | 填写 |
|---|---|
| 机型 | `QA42 Fleet A`：Type Code `B738`，Type `pax`，Max Passengers `180`，Parameter IDs `P` |
| 机型 | `QA42 Fleet B`：同上，名称改为 `QA42 Fleet B` |
| 机场 | `XXXX`，Name `QA42 Airport` |
| 飞机 | Name `QA42 Plane`，Registration `B-QA42`，Fleet ID 选 `QA42 Fleet A`（本地新建的机型） |
| 航路 | ZSPD → ZSSS，Route String `DCT`，Remarks `QA42 routing` |
| 航线 | Type `scheduled`，Callsign `CCA9042`，Flight Number `CA9042`，ZSPD → ZSSS，Fleet IDs 选 `QA42 Fleet A`，Start Date 今天，End Date 今天起一个月内，起降时间任填（如 `10:00`／`11:30`），Remarks `QA42 scheduled` |
| 航线 | Type `jumpseat`，ZSPD → ZSSS，Callsign `CCA9043`，Flight Number `CA9043`（服务器要求填写，但不会保存），Remarks `QA42 jumpseat`；**Fleet IDs 留空**（服务器不保存 jumpseat 的机型，填了会被标出） |

预期：7 项全部成功；飞机和 scheduled 航线在机型之后执行，自动用上新机型的远端 ID；每项都有回读核对。

## 第 2 轮：修改（一次提交）

| 资源 | 修改 |
|---|---|
| 机场 `XXXX` | Name 改为 `QA42 Airport renamed` |
| 机型 `QA42 Fleet A` | Name 改为 `QA42 Fleet A renamed` |
| 飞机 `B-QA42` | Name 改为 `QA42 Plane renamed`（**只改名称**） |
| 航路 | Remarks 改为 `QA42 routing changed` |
| scheduled 航线 | Remarks 改为 `QA42 scheduled changed`；Fleet IDs 加上 `QA42 Fleet B`（A → A、B） |
| jumpseat 航线 | Remarks 改为 `QA42 jumpseat changed` |

预期：6 项全部成功。

## 第 3 轮：飞机移动

飞机 `B-QA42` 的 Fleet ID 从 `QA42 Fleet A renamed` 改为 `QA42 Fleet B`，**不改其他字段**，单独提交。

预期：成功，并在新机型下回读到这架飞机。

## 第 4 轮：清理（一次提交）

在各资源页把本次新建的 7 条记录都标为删除：两条航线、航路、飞机、两个机型、机场 `XXXX`。一次提交。

预期：7 项全部成功；程序先删航线、航路、飞机，再删机型，最后删机场。删除是软删除，成功条目会注明 204 回执与之后读不到（404）。

最后“从 API 刷新”一次，确认各资源里已没有 `QA42` 记录。

## 顺带确认（不要刻意制造）

阶段 1 的几项自动处理只在出现对应故障时才会触发，正常验收多半看不到：回读不一致后再读一次、本轮结束时核对结果未知的条目、自动找回新建记录的 ID、无回执删除的核对、以及 E7 的 429 本轮再发一次。如果验收中任务中心出现这些提示，截图并记下时间即可。

## 完成后

告诉开发者四轮的结束时间（大致即可）。开发者只读打开本地工作区数据库（不修改），从任务记录中整理每次请求的时间、方法、路径、状态码和结果，写入能力矩阵的“软件真实流程验证”列和“软件真实写入记录”一节。凭据和令牌不会出现在任务记录里。
