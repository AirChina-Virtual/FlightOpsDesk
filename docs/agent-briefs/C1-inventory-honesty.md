# C1：清单测试改为如实记录位置

先读 [清理共用做法](C0-cleanup-common.md)，以及其中引用的通用约束和架构速览。

## 工作树与分支

- 工作树：`F:\vCCA\wt-c1-inventory-honesty`
- 分支：`cleanup/c1-inventory-honesty`，起点见派活消息
- 并行任务可能在处理其他旧测试文件；只改本简报列出的文件、`tests/VamSys.Tests/Program.cs` 中对这些文件的调用，以及下面的新测试目录。

## 负责的文件

- 旧测试：`tests/VamSys.UnitTests/Inventory/` 及其 `Expected/`
- 新测试目录：（不新增测试目录）

## 说明

清单测试里有两处为了让预期文件不变而“映射回旧位置”：`SourceScan.Relative` 把新路径改写成 `src/VamSys.Core/...`（R2 拆分时加入），`EnumInventoryTests` 把 Domain、Application 程序集标成 `VamSys.Core`，并为从 App 移到 Application 的 `ReviewDisposition` 加了来源映射（R4 加入）。预期文件因此记录的是已经不存在的位置。

1. 删除这些映射，让清单如实记录当前的文件路径和程序集名。
2. 用 `VAMSYS_UPDATE_GOLDEN=1` 只重新生成受影响的清单预期文件；用 git diff 逐行确认：变化只有路径、程序集名或类型来源，枚举成员名与数值、JSON 属性、资源键、能力键文本本身都没有变。只改换行符的文件恢复原样。
3. 在 `Inventory/` 的测试里加注释说明：位置变化时允许重新生成，但成员、数值、键必须不变。
本简报不删除旧场景，也不受“共用做法”中迁移步骤约束，其余约束照样适用。提交说明：`清理：清单测试如实记录当前位置`。
## 完成标准

- 本简报负责的旧场景全部迁移并删除（保留项除外），或在结果中列出剩余项。
- `pwsh scripts/check-all.ps1` 以 `ALL PASSED` 结束。

## 结果（由子代理填写）

删除前／后旧运行器场景数：未改动（本简报不涉及旧运行器）
已删除场景：无
新增测试：无
改动：
- SourceScan.Relative 不再把 Domain/Application/Infrastructure 的路径改写成 src/VamSys.Core/...，直接返回当前相对路径。
- EnumInventoryTests 删除 MovedFromApp 来源映射，程序集名取真实名称（VamSys.Domain/Application/Infrastructure）；ItemState 的断言改为不依赖程序集前缀。
- Golden.cs 与 EnumInventoryTests 加注释：位置变化时允许重新生成，但成员、数值、键必须不变。
重新生成的预期文件（仅 3 个，其余只改换行符的已确认未入 diff）：
- Enums.txt：只有程序集前缀变化（Core -> Domain/Application/Infrastructure），ReviewDisposition 由 "VamSys.App(source) ... declared in" 变为真实程序集 VamSys.Application:VamSys.Core.ReviewDisposition（成员 Ready0/Deferred1/Blocked2/AwaitingConnection3 不变）；去掉前缀后的成员与数值逐行比对，除上述来源差异外无差别。
- CapabilityKeys.txt：4 行仅路径 VamSys.Core/ApiEvidence.cs -> VamSys.Domain/ApiEvidence.cs，键文本不变。
- Constants.txt：2 行仅路径 VamSys.Core/AppPaths.cs -> VamSys.Infrastructure/AppPaths.cs，字面量不变。
其余清单文件（JSON、资源键、自动化 ID、合同哈希）未变化。
保留的场景及原因：无
检查：pwsh scripts/check-all.ps1 ALL PASSED（unit 1387 通过 1 跳过，integration 187 通过 1 跳过）
提交：见下方
