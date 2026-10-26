# 子代理任务简报

每个交给子代理的重构任务都写成这里的一个文件。派活时只需告诉子代理“读 `docs/agent-briefs/<文件>.md` 并执行”；中断后重新派出也只需同一句话。子代理完成后在文件末尾的“结果”一节写报告，审核时读这一节即可。

## 规则

- 从 [TEMPLATE.md](TEMPLATE.md) 复制，文件名用 `<阶段>-<简短名称>.md`，例如 `R4-workspace-session.md`。
- “负责的文件”写清目录；并行的两份简报不能列出同一批文件。
- 只把与任务有关的背景写进来；通用约束（不变规则、金样、git 与 dotnet 用法、检查命令）由模板引用，不在每份简报里重复。
- 性能计时由主会话在阶段结束、机器空闲时统一测量；子代理只报告分配量、写入行数、请求数等不受机器负载影响的数字。
- 模型：设计与实现用 Sonnet；跑测试、跑脚本、汇总结果、机械替换用 Haiku。

## 通用约束（所有简报都适用）

0. **先读 [架构速览](../ARCHITECTURE.md)**，再只读简报“必读”里列出的文件；不要通读整个代码库。任务控制在约 30–60 步工具调用以内，做完就交回。
1. **只在指定的工作树里工作**（`F:\vCCA\wt-<名称>`，分支见简报）。临时文件只放在该工作树的 `artifacts/scratch`。
2. **工具：** dotnet 由 `scripts/check-all.ps1` 自动定位（工作树没有 `.tools` 时使用主检出 `F:\vCCA\vamsys\.tools\dotnet`）。git 用 `$git="$env:LOCALAPPDATA\GitHubDesktop\app-3.6.6\resources\app\git\cmd\git.exe"; function g { & $git -c safe.directory=* @args }`；不改全局配置，不推送，不用 `git stash`。
3. **检查：** 每次提交前运行 `pwsh scripts/check-all.ps1`（快速档，约 1–2 分钟）；交回前运行一次 `pwsh scripts/check-all.ps1 -Mode Full`。两者都必须以 `ALL PASSED` 结束。工作树中旧测试比主检出少 1 项（缺 `artifacts/step5-v3-baseline`），属正常。
4. **不可变的东西：** 请求、存储、CSV 金样（`tests/VamSys.IntegrationTests/Golden`）逐字节不变，不设置 `VAMSYS_UPDATE_GOLDEN`；清单测试的预期文件只有简报明确允许的变化，且要用 git diff 确认只改了允许的部分。[重构计划](../REFACTOR-PLAN-2026-10-20.md) 第 3.2 节的不变规则全部适用。
5. **提交：** 每完成一个绿色的小步就提交，提交说明用中文，以阶段号开头（如 `R4：…`）。行为修复与结构调整分开提交。
6. **旧测试：** 只有当新测试已覆盖同一需求时才删除旧场景，删除前读完它的断言，并在 [TEST-COVERAGE.md](../TEST-COVERAGE.md) 记录对应关系。
7. **遇到产品缺陷：** 记录并报告，不顺手修；需要的测试先跳过并写明原因。
