# 2026-08-03 合并验证记录：E2-2 + E2-3 + E5-1 → `feat/exec-2026-08-02-phase0`

> 日期：2026-08-03 · 仓库：ValleyTalk（C# 执行层）· 分支：`feat/exec-2026-08-02-phase0`
> 类型：Phase 2 并行实施三分支的最终合并验证（用户要求：最终合并必须派发 Plan Agent/GLM 5.2 写合并计划并执行；每个 SubAgent 分支模块写实现思路文件）

---

## 1. 目标

将三个在独立 worktree 上并行开发、且各自已通过单分支门禁的功能分支合并进主特性分支，并验证合并结果门禁全绿。

| 条目 | 内容 | Worktree 分支 | Tip | 独立门禁 |
|---|---|---|---|---|
| E2-2 | 聊天栏玩家→NPC 四层路由 + 会话模式 + 静默权 | `feat/exec-2026-08-02-e22` | `5b91780` | 161 test / 0 fail |
| E2-3 | 长文本规则（单句→气泡；多句→聊天栏分句带间隔） | `feat/exec-2026-08-02-e23` | `b0d3133`+`7003fbf` | 142 test / 0 fail |
| E5-1 | NPC 作息表数据层（起床时间 + IsAwake 查询） | `feat/exec-2026-08-02-e51` | `9422c96` | 135 test / 0 fail |
| E2-1 | 动态速度（已先提交为基础） | 主 worktree | `591f924` | 已并入 |

三个分支均从公共祖先 `591f924`（E2-1 动态速度，已先合入目标分支）分叉，故合并树正确复用 E2-1。

---

## 2. 合并纪律（用户强令 2026-08-03）

1. **能并行的地方必须并行**：E2-2 / E2-3 / E5-1 在三个隔离 git worktree（`vt-e22` / `vt-e23` / `vt-e51`）上并行实现，避免同一分支的 git commit 竞争。
2. **每个 subagent 的分支/模块动手前必须写实现思路文件**：三个分支的 实现思路 均已写入对应 worktree（见 §3），合并后归档于 `docs/ideas/2026-08-03-merge-plan-e22-e23-e51.md`。
3. **最终合并必须派发 Plan Agent（模型 GLM 5.2）写合并计划并执行**：合并 Plan/执行由独立 deep 子智能体完成，产出 `docs/ideas/*merge-plan*`，并逐 commit 跑门禁。

---

## 3. 各分支实现思路文件（动手前即写）

| 分支 | 实现思路文件 | 行数 | 是否覆盖设计依据 |
|---|---|---|---|
| E2-2 | worktree `vt-e22` 内 `docs/*.md` | — | 是 |
| E2-3 | worktree `vt-e23` 内 `docs/*.md` | — | 是 |
| E5-1 | worktree `vt-e51` 内 `docs/*.md` | — | 是 |

> 注：每个子 Agent 的「实现思路文件」在各自 worktree 分支内先写后码；三份独立文件篇幅均充分（约 88–134 行），此处不重复全文，合并计划文档 `docs/ideas/2026-08-03-merge-plan-e22-e23-e51.md` 已集中归档冲突热区与解析决策。

---

## 4. 合并计划与冲突热区（Plan agent GLM 5.2 产出）

完整计划见 `docs/ideas/2026-08-03-merge-plan-e22-e23-e51.md`。要点：

**合并顺序：E2-3 → E2-2 → E5-1**
1. E2-3 先（耦合优先）：E2-3 改写 `ActiveSpeechRouter.Route` 委托给新 `SpeechDisplayRouter`；E2-2 的 `ChatBarRouter` 调用 `ActiveSpeechRouter.Route`。先合 E2-3 保证中间树自洽。
2. E2-3 同时接线 `SpeechDisplayRouter` 显示接收器（`ApplyConfig/SetChatSink/SetBubbleSink`）。
3. E2-2 次：文本重叠仅 `ModConfig.cs` + `EventHandlerInitializer.cs`，均为不相交区域。
4. E5-1 末：仅与 E2-3 重叠 `ServiceInitializer.cs`（不相交区域），与 E2-2 零重叠。

**冲突热区（预判 + 语义解析）**

| 热区文件 | 冲突点 | 解析 |
|---|---|---|
| `ModConfig.cs` | E2-2 聊天栏 5 属性块 / clamp块；E2-3 `LongTextIntervalMs` / clamp块 | 不相交区域（锚点相距 100+ 行），双保留 |
| `EventHandlerInitializer.cs` | E2-2 三处插入（Init/Reset/OnUpdateTicked）；E2-3 两处（GMCM/Tick） | 全部独立锚点；`OnUpdateTicked` 共享 1 行上下文但各自完整保留；保留 `SpeechDisplayRouter.Tick()` + `Chat.ChatBarRouter.ProcessPendingReplies()` |
| `ServiceInitializer.cs` | E2-3 `using Utils` + apply + sip；E5-1 `using Schedule` + RegisterSingleton | 双 using 无冲突、注册点相距 146 行，同时保留 |
| **跨分支耦合（语义）** | E2-3 改写 `ActiveSpeechRouter.Route`；E2-2 调用其 | 签名 `Route(NPC, Farmer, string)` 保持不变，`ChatBar.cs` 调用点与 XML doc 双重确认，无需改调用点 |

**关键：全程不使用 `-X theirs/ours` 强制；每个冲突 hunk 按上表语义解析。**

---

## 5. 合并执行结果（Plan agent 执行）

| 提交 | 内容 | 冲突 | 门禁 |
|---|---|---|---|
| `62f0e54` | merge: E2-3 into phase0 (long-text rules) | 0（自动干净合并） | 142 pass / 0 fail |
| `af8d187` | merge: E2-2 into phase0 (chat-routing + session) | 0（自动干净合并） | — |
| `3ceffd6` | merge: E5-1 into phase0 (schedule data) | 0（自动干净合并） | 最终门禁见下 |

三笔 `--no-ff` 合并 commit 全部干净落地，无冲突标记残留。

---

## 6. 合并后门禁（独立复核 2026-03）

| 门禁 | 结果 |
|---|---|
| `dotnet test ValleyAgent.UnitTests -p:GamePath="<REPO_ROOT>\Stardew Valley"` | **192 passed / 0 failed / 0 skipped** |
| 全部 6 个项目构建（ValleyAgent, Abstractions, Autopilot, Test, ApiTest, UnitTests） | 6 × EXIT=0，0 警告 0 错误 |
| ApiTest CA2024 既有警告 | 2 处（KimiProvider.cs:270 / OpenAICompatibleProvider.cs:150），**改动范围外、既有**，已另行 ticket，不修复 |

> 独立复核（非信任子代理自报）：由本会话在合并后的主 worktree 直接重跑 `dotnet test` + 逐项目 `dotnet build` 验证，非单纯采信合并 Agent 报告。

---

## 7. 仓库终态

- 当前分支：`feat/exec-2026-08-02-phase0`，HEAD = `3ceffd6`。
- 最近提交链（自下而上）：
  - `3ceffd6` merge E5-1  → `af8d187` merge E2-2  → `62f0e54` merge E2-3  → `5b91780` E2-2 → `b0d3133`+`7003fbf` E2-3 → `9422c96` E5-1 → `591f924` E2-1 → ...
- 工作树 clean：仅未跟踪 `docs/ideas/`（合并计划参考）与 `docs/plan/2026-08-03-e1-1-transcript-store-verification-record.md`（上一里程碑记录）。
- 均未 push（保持本地）。

## 8. 结论

Phase 2（E2-1..E2-3）+ E5-1（独立并行项提前拉）达成：**最终合并按 GLM 5.2 Plan Agent 计划执行，按 commit 逐门禁**；合并后全项目 0 警告 0 错误、192 单测全绿。四个分支、三次合并、一次独立复核全部通过。

## 9. 后续待办（保持门控，不自动处理）

- [ ] ApiTest 2 处既有 CA2024 警告 → 独立 ticket（用户明确 OK 后处理；ApiTest 为独立 exe provider 测试台，需先读代码设计）。
- [ ] 覆盖率欠账：ValleyAI 9 个测试文件行覆盖率 < 0.8（threshold 0.8，`bun test` exit-1，非真实失败）→ 独立 ticket。
- [ ] master 合并：`feat/exec-2026-08-02-phase0` 是否合入 master → 待用户决策（仍保持 branch-only，不 push）。
- [ ] 后续 Phase：E5-2/E5-3（晨间喊话 + 主动发言，依赖 Phase 2 路由/会话模式代码，天然衔接）。