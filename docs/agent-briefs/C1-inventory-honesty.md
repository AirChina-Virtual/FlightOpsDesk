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

按共用做法中的格式，不超过 25 行。