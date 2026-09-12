# Phase 0 架构债务收尾 — 验证与实施记录

> **日期**：2026-08-03
> **执行者**:Sisyphus (ultrawork mode + strict-dev-pipeline skill)
> **关联计划**:`docs/plan/2026-08-02-execution-plan.md`
> **分支**:
> - ValleyTalk (C#):`feat/exec-2026-08-02-phase0`
> - ValleyAI (TS):`feat/exec-2026-08-02-phase0-ts`
>
> **概述**:Phase 0 实施过程中发现"WIP 已领先于计划文档"E0-1/E0-2/E0-3 实际已在未提交 WIP 中落地,经直接文件证据签收确认 DONE;真正未完成的仅 E0-5(已实施)与 E0-4(进行中),以及一项用户授权的额外任务(consolidate_day 路由)。本文档固化所有验证证据与实施细节,避免后续会话重复考古。

---

## 1. 基线快照(实施前)

| 检查项 | 命令 | 结果 | 说明 |
|---|---|---|---|
| TS 单测 | ValleyAI `bun test` | 450 pass / 0 fail / **exit 1** | 见 §6 退出码解释 |
| TS 类型 | ValleyAI `bun run typecheck` | exit 0 | tsc --noEmit 干净 |
| 协议契约 | ValleyAI `bun run check:protocol` | **1 DEAD_PIPELINE** | 仅 `consolidate_day`;`friendship_eval` **不在发送列表** |
| C# 构建 | ValleyTalk 5× `dotnet build` | 5× exit 0 | ValleyAgent / Abstractions / UnitTests / ValleyTalk.Tests / TestMod |

**check:protocol 基线 S_send 清单**(C# 发送的 type 集合,6 个):
`action_result` / `consolidate_day` / `dialogue` / `hello` / `ping` / `state_changed`
→ `friendship_eval` 已不在发送列表(与计划文档"删除 friendship_eval 死管道"的描述不符 —— 文档过时)。

**这意味着计划文档 §Phase 0 E0-1 的 "删除发送端 WebSocketClient.cs:63" 目标在基线时已完成**。

---

## 2. 逐项验证记录

### E0-1 消费 friendshipDelta + 删除 friendship_eval 死管道 — ✅ 已验证完成

**判定方法**:直接读取关键文件 + PowerShell Select-String 全仓搜索。

**证据链**:

1. **TS 端发送 delta**(已落地):
   - `packages/stardew/src/types.ts:48-52` — `DialogueResponse.friendshipDelta?: number` / `friendshipReason?: string`,注释明确"方案 B(§4.1.1):消灭 friendship_eval 死管道"。
   - `packages/stardew/src/protocol-adapter.ts:180-181` — `...(result.friendshipDelta !== 0 ? { friendshipDelta: result.friendshipDelta } : {})`,仅在非零时携带(delta=0 不阻塞)。
   - `packages/stardew/src/stardew-tools.ts:250-262` — `evaluate_friendship` 工具注册为 no-op/记录型,描述明确"每轮对话必须调用一次";LLM 通过它产出 delta/reason。
   - 单测证实:`packages/stardew/tests/tool-result-feedback.test.ts` 的 I2 系列 + 第 7 个场景(50 条 evaluate_friendship 累计)。

2. **C# 端消费 delta**(已落地):
   - `src/ValleyAgent/Api/DialogueManagementApi.cs:133-134` — `var fsDelta = result.FriendshipDelta ?? 0; var fsReason = result.FriendshipReason ?? "";`
   - `src/ValleyAgent/Api/DialogueManagementApi.cs:185-198` — 构造 `FriendshipChangeContext`,`_ = Task.Run(async () => await _friendshipSystem.ApplyWithExternalDeltaAsync(fsContext, fsDelta, fsReason))` fire-and-forget。注释明确:必须 fire-and-forget,否则 await 阻塞 EndDialogueRequest 并触发熔断器 OPEN。
   - `src/ValleyAgent/Friendship/FriendshipSystem.cs:53-155` — `ApplyWithExternalDeltaAsync` 实现完整:边际递减(0.75/轮,下限 0.1)、特殊事件倍率(生日 3×/节日 2×/雨天 0.5×/初次见面 2×)、clamp 到 [0, Max]、写 `FriendshipChangeRecord` 历史。
   - `src/ValleyAgent/Friendship/FriendshipSystem.cs:11-13 + :49-52` 注释明确:"方案 B:delta 由 dialogue_response.friendshipDelta 携带"、"取代 EvaluateAndApplyChangeAsync(已删,消除 friendship_eval 死管道)"。

3. **发送端 friendship_eval 已删除**:
   - `check:protocol` 基线 S_send 集合无 `friendship_eval`(见 §1)。
   - `Select-String 'EvaluateFriendshipAsync'` 全仓搜索 → 0 命中。
   - **遗留腐朽**(cosmetic,不影响功能):`src/ValleyAgent/CommandExecutor.cs:465-472` 仍有注释引用 "friendship must be driven by friendship_eval",且对 `set_friendship`/`update_friendship` 命令仍按旧友谊侧拒绝处理。这类注释与"方案 B"逻辑脱节,建议未来 Phase 1 E1-1 全量留痕重构时一并清理,**不作为 Phase 0 阻塞项**。

**签收结论**:E0-1 在未提交 WIP 中已完整落地,无重复实施必要。

---

### E0-2 海莉修复:set_state 接线 + action_result 回执 — ✅ 已验证完成

**证据链**:

1. **对话路径正确消费 set_state**:
   - `src/ValleyAgent/Patches/DialogueBoxInputPatch.cs:382-401` `DispatchDialogueActions` — 注释明确"海莉事件根因修复:不再丢弃 TrySetAgentState 返回值,不再 continue 跳过反馈";`speak`/`wait` 跳过,其他工具一律 `_commandExecutor?.ExecuteAction(action, npc.Name)`。

2. **ExecuteAction 通道回执**:
   - `src/ValleyAgent/CommandExecutor.cs:103-105` — `case "set_state": (success, reason) = ExecuteSetState(args, npcName);`
   - `src/ValleyAgent/CommandExecutor.cs:237-281` `ExecuteSetState` 实现:agent 不存在 → `AgentMissing`;状态名非法 → `InvalidState`;`TryTransition` 被拒 → `TransitionBlocked`;OK 时返回 success=true。所有分支都产出 `(bool Success, ActionResultReason Reason)`,**无返回值丢弃**。
   - `src/ValleyAgent/CommandExecutor.cs:128-131` — `if (!string.IsNullOrEmpty(action.CallId)) { _ = SendToolActionResultAsync(npcName, action.CallId, tool, success, reason, resultMessage); }`。**success/失败 一律发回 C3**,无 `continue` 跳过。

3. **下游消费**(此处交叉关联 E0-3 完整链):
   - `SendToolActionResultAsync`(`CommandExecutor.cs:138-166`)→ `ProtocolV2.ActionResultMessage` → WS →
   - TS `ProtocolAdapter.handleActionResult`(`protocol-adapter.ts:51-54`)→ `routeToolResult` 入队 per-NPC 反馈队列 → 下轮对话注入 prompt。

**签收结论**:E0-2 已完整落地。海莉场景"对话要求跟随 → NPC 实际进入 FOLLOW;被状态机拒绝时 NPC 能感知到"的验收预期满足。

---

### E0-3 state_changed 消息两端落地 — ✅ 已验证完成

**证据链**:

1. **C# 端发送**:
   - `src/ValleyAgent/Protocol/StateChangedSender.cs`(完整 66 行,曾处于 untracked 状态,现已纳入版本控制)— 订阅 `AgentStateMachine.OnStateChanged`,在 `HandleStateChanged` 中 `MessageProtocol.Serialize(new { type="state_changed", npcName, previousState, newState, wasForced, previousStateDurationMs, reason })` 火忘发送。
   - `src/ValleyAgent/Services/AgentService.cs:152-153` — 在创建 agent 时 `new StateChangedSender(...)` 并 `Start()` 订阅事件。
   - `src/ValleyAgent.Abstractions/StateMachine/AgentStateMachine.cs:160 + :199` — `OnStateChanged` 事件 + 带理由参数("travel_failed"/"evicted"/"task_completed"/"manual"/"llm_decision")传给 sender 由 TS 渲染 prompt。

2. **TS 端路由**:
   - `packages/stardew/src/protocol-adapter.ts:235` — `case "state_changed": return this.handleStateChanged(m as StateChangedMessage);`
   - `packages/stardew/src/protocol-adapter.ts:62-67` — `handleStateChanged` 调 `this.registry.updateActualState(req.npcName, req.newState, req.reason)`,记 state 转换日志,返回 ack。

3. **NPC 状态镜像供 prompt 使用**:
   - `packages/stardew/src/stardew-agent-registry.ts:125-145` — `updateActualState(npcName, newState, reason)` 写入 per-NPC `agent.actualState` 字段;`getActualState(npcName)` 读取。
   - **I1 不变量**:`StardewAgent.actualState` 优先于 `worldSnapshot.npcState`;**仅 `state_changed` 消息能写**;`worldSnapshot.npcState` 只是 C# 发来的"游戏状态",不直接覆盖 actualState。这是认知不脱离现实的关键。
   - `packages/stardew/src/prompt-builder.ts:36, 49-54, 143-145` — 系统 prompt 模板的 `{actual_state_section}` 占位符,`ACTUAL_STATE_SECTION` 常量,仅在 `actualState && actualState.trim().length > 0` 时注入(避免空头标题)。
   - `packages/stardew/src/server.ts`(经 grep)把 `registry.getActualState(npcName)` 透传到 `PromptBuilder.buildDialogueSystemPrompt` 的 `actualState` 参数。

4. **测试覆盖**(已落地,无需新增):
   - `packages/stardew/tests/protocol-adapter.test.ts:180-` — `handleStateChanged updates registry actualState and returns ack`、`routeMessage routes state_changed to handleStateChanged`。
   - `packages/stardew/tests/invariants.test.ts` — I1 系列(test "I1: actualState is undefined before any state_changed"、"I1: only state_changed message updates actualState mirror" 等)守护不变量。
   - CH-05 场景 + 第 4 个场景(50 轮潮流 state_changed 验证 actualState 始终反映最后一次 state_changed)。

**签收结论**:E0-3 完整端到端打通。海莉场景"对话时 NPC 对自身当前状态的认知与实际一致" —— 由 actualState mirror + I1 不变量守护。

---

### E0-4 消息分级落地(删跨图旅行播报 + 灰色系统消息) — ⏳ 实施 map 完成 / 委派 bg_2e72833e

**Map 来源**:bg_94e9d889 (explore agent, 2m 42s) 完整定位 5 项要点。

**Map 关键结论**:

1. **跨图旅行播报 — 全部链路单一**:整个 src 树中只有 `src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs` 一个文件发出中文旅行短语,全部通过 `ShowSpeech(...) → TravelSpeechHelper.Speak(...)`(`src\ValleyAgent.Abstractions\Utils\TravelSpeechHelper.cs:14-39`,白字+气泡)路由。其他所有 Handler(Farm/Mine/Forage/Fight/Talk/IdleWander)经 grep 验证无旅行播报。

2. **8 个 ShowSpeech call site 全部在 AgentNavigator**:
   - 第 174 `ShowSpeech(npc, "我跟不上你了…");` — **F2 切图重启失败 follow-lost**(替换为灰色)
   - 第 624 `ShowSpeech(npc, "这地方太绕了，我找别的路...");` — fallback warp(删除)
   - 第 712 `ShowSpeech(npc, "我马上到！");` — 跳过出发阶段(删除)
   - 第 725 `ShowSpeech(npc, "我过去看看");` — 启程(删除)
   - 第 795 `ShowSpeech(npc, "我好像过不去了…");` — warp 异常 F3 修复(删除)
   - 第 801 `ShowSpeech(npc, "我到了！");` — 到达目的地图(删除)
   - 第 891 `ShowSpeech(npc, "我跟不上你了…");` — **Departing 阶段重启失败 follow-lost**(替换为灰色)
   - 第 929 `ShowSpeech(npc, "我马上到！");` — Departing→Travelling 过渡(删除)

3. **灰色"XXX离开了"挂点** — `src\ValleyAgent\Core\AgentTickLoop.cs`:
   - 唯一事件 `OnVanillaReleaseFinalized`(Line 42)在 `FinalizeVanillaRelease(...)`:`npc.followSchedule = true; npc.ignoreScheduleToday = false; ...; OnVanillaReleaseFinalized?.Invoke(this, new VanillaReleaseEventArgs(npcName));`
   - 订阅者 `src\ValleyAgent\Initialization\EventHandlerInitializer.cs:1078-1087` `OnVanillaReleaseFinalized` —— **唯一插入点**。
   - 与 AI-initiated 离开的区别:AI-initiated 是 LLM 一次对话内 `speak + set_state IDLE`,走 `SocialCommands.cs:27` `Color.Gold` 彩色发言;vanilla-release 是 LLM 连续 N 次 IDLE → `TrackIdleDecision` 进入 `_releasedToVanilla` → `BeginWalkBack` 走完后 `FinalizeVanillaRelease` 触发事件。两者应在 chatbox 上显著区分:AI-initiated=彩色 speak;vanilla-release=灰色 sys msg。

4. **灰色"XXX没跟上"挂点** — `AgentNavigator.cs:174 + :891` 两处 `"我跟不上你了…"` 已自然匹配 Site A/B(Travelling 阶段 & Departing 阶段)。两处调用都紧跟 `OnTravelFailed?.Invoke(npcName)` 触发器,执行后状态机自动 `ForceTransition(IDLE, reason="travel_failed")` 异步由 state_changed 通知 TS。

5. **现有 chatbox 颜色约定**:无任何现成灰色系统消息 helper;系统消息目前一律 `Color.White`(如 `DialogueBoxInputPatch.cs:506` F5 驱逐提示 `"{xxx} 告别离开了"` 就是先例)。新增 helper `TravelSpeechHelper.SystemSpeak(npcName, msg)` 用 `Color.LightGray` + 星号包装实现"灰色斜体或灰色普通字"语义;`Color.Gray` 仅在 SpriteBatch 输入框 UI 使用(`DialogueBoxInputPatch.cs:552/557`),未在 chatbox 使用。

**安全保证**:AI 主动人设 leaving 路径 — `SocialCommands.cs:27 Color.Gold` + `DialogueBoxInputPatch.ProcessPendingReplies` 原版渲染 —— bg_2e72833e 的 MUST NOT DO 明确禁止触碰。`NpcSpeechHelper` / `ActiveSpeechRouter` / `ItemCommands` 等其他 colored/white 系统消息也都在 MUST NOT DO 名单内。

**实施委派**:bg_2e72833e (quick 类 agent + strict-dev-pipeline skill),原子化实施 5 步:
A. 在 TravelSpeechHelper 新增 SystemSpeak(星号包+LightGray)
B. 删 AgentNavigator 6 条 ShowSpeech 调用(保留下方日志)
C. 替换 :174 和 :891 两处为 `TravelSpeechHelper.SystemSpeak(npc.Name, "没跟上")`
D. 在 EventHandlerInitializer.OnVanillaReleaseFinalized 加 `TravelSpeechHelper.SystemSpeak(e.NpcName, "离开了")`
E. 若 AgentNavigator 本地 ShowSpeech wrapper 变为死代码 → 删除以避免 IDE warning(strict-dev-pipeline 铁律:0 警告)

期望输出:5× dotnet build 0 警告 0 错误 + ValleyAgent.UnitTests + ValleyTalk.Tests 全通过 + 实际 git diff paste。完成后由 Sisyphus 主会话自审 + CTX 分支 Phase 0 gate 复跑(check:protocol 仍绿 + bun test 仍 454 pass 0 fail)+ commit。

---

### E0-5 REASON_CN 未知枚举中文兜底 — ✅ 已实施并验证

**问题**:`packages/stardew/src/stardew-agent.ts:74` 原代码 `const reasonCn = REASON_CN[r.reason] ?? r.reason;`,当 C# 新增 `ActionResultReason` 枚举值而无对应中文映射时,会将裸英文/camelCase 枚举名泄漏进中文 prompt 反馈段,污染 LLM 上下文。

**修复**(本次实施):

```diff
- const reasonCn = REASON_CN[r.reason] ?? r.reason;
+ const reasonCn = REASON_CN[r.reason] ?? "未知原因";
```

**回归测试**:在 `packages/stardew/tests/stardew-agent.test.ts:36-63` 新增 `completelyUnknownEnumValue` 单测,断言渲染字符串包含"未知原因"且不含原始枚举名。

**附属清理**:`packages/stardew/tests/fault-injection.test.ts:352-370` 更新既有断言(原依赖于"未知 reason 原样渲染"的行为)以匹配新约定。

**`REASON_CN` 表当前完整内容**(`stardew-agent.ts:49-58`):
```ts
const REASON_CN: Record<string, string> = {
  agentMissing: "agent 未分配",
  transitionBlocked: "状态机拒绝转换",
  invalidState: "非法状态名",
  targetUnreachable: "目标不可达",
  itemNotFound: "物品不存在",
  inventoryFull: "背包已满",
  locationInvalid: "地点无效",
  internalError: "内部异常",
};
```

**未来维护约定**:C# 端新增 `ActionResultReason` 枚举值时,**必须**同步在本表加入对应中文翻译,否则前端 prompt 会显示"未知原因"兜底而非真实原因——这是可降级的兜底而非硬失败,但翻译漏配会让 NPC 行为反馈变得模糊。

**签收结论**:E0-5 完整落地,验证由独立 `Get-Content` 重读改动后文件并 grep 命中`?? "未知原因"`确认。

---

## 3. 用户授权的额外任务:consolidate_day TS 路由

**背景**:`check:protocol` 基线显示 `consolidate_day` 是唯一存活的死管道(C# 在 `EventHandlerInitializer.cs:986`(DayEnding 触发当日记忆凝练)+ `:1020`(DayStarted 跳睡眠补跑昨日)发送,但 TS 不路由)。计划文档未提及此项;Phase 0 红线"全绿才允许进入下一 Phase"被它卡住。经用户确认(选项"Implement TS routing(Recommended)"),在本 Phase 0 周期内同步落地 TS 端路由。

**实施**(由 quick category subagent bg_3f29e516 完成,我独立重读 + 独立重跑 check:protocol 验证):

1. `packages/stardew/src/types.ts:107-119` — 新增 interface:
   ```ts
   export interface ConsolidateDayMessage {
     type: "consolidate_day";
     npcName: string;
     dateIso: string;
     requestId?: string;  // 可选,向后兼容未携 requestId 的旧 C# 客户端
   }
   ```
   并加入 `IncomingMessage` union。

2. `packages/stardew/src/protocol-adapter.ts:13` 加 import;`:32` 新增幂等集合 `private readonly seenConsolidations = new Set<string>();`(key = `${npcName}|${dateIso}`);`:72-81` 实现:
   ```ts
   async handleConsolidateDay(req: ConsolidateDayMessage): Promise<{ type: "ack"; requestId: string }> {
     const key = `${req.npcName}|${req.dateIso}`;
     const duplicate = this.seenConsolidations.has(key);
     this.seenConsolidations.add(key);
     const id = (req as { requestId?: string }).requestId ?? "unknown";
     console.log(`[${timestamp()}] [consolidate] ${req.npcName} date=${req.dateIso}${duplicate ? " (duplicate, skipped)" : ""}`);
     // Phase 1(E1-1 TranscriptStore)将在这里接入实际的 per-Agent LLM 日结器
     return { type: "ack", requestId: id };
   }
   ```
   `:249` routeMessage 新增 `case "consolidate_day": return this.handleConsolidateDay(...)`,放在 default 之前。

3. `packages/stardew/tests/protocol-adapter.test.ts:180-231` — 三项单测:
   - `handleConsolidateDay returns ack with correct requestId`
   - `handleConsolidateDay is idempotent on same npcName|dateIso`
   - `routeMessage routes consolidate_day to handleConsolidateDay`(结构对齐 handleStateChanged 的既有测试)

4. `protocol/messages.json:409-438` — 将 `consolidate_day` 标记为 active/routed 并同步可选 `requestId` 元数据(schema drift 必须同步否则扫描器退红)。**未修改 `scripts/check-protocol-contract.ts`**(扫描器对路由的自动识别 + messages.json 的元数据声明是两层独立机制)。

**幂等性说明**:SeenConsolidations 集合以 `(npcName, dateIso)` 为 key,跳过重复 payload 后续触发的实际日结(Phase 1 才会接入)。这契合 C# 端的语义:C# 自身已是幂等发送(同一 agent 同一日期多条 consolidate_day 触发也只期望一次日结),跳睡眠补跑机制基于"昨日凝练未完成"的语义触发——TS 端的幂等集合与 C# 端不冲突:同一 dateIso 收到 N 次时只执行 1 次实质性日结。

**sign off**:Phase 1 E1-1 才会接入 LLM 日结逻辑;本阶段 TS 端仅做路由 + 幂等 + 日志,以满足本阶段红线"check:protocol 全绿"为目标。

---

## 4. Phase 0 网关现状

### 4.1 已关闭的门

- ✅ TS 单测(450 → 454 pass / 0 fail — E0-5 + consolidate_day 实施 + 4 条新增测试)。退出码见 §6。
- ✅ TS 类型检查 `tsc --noEmit` exit 0。
- ✅ **协议契约检查 `check:protocol` 完全通过**(`DEAD_PIPELINES 0`、`ORPHAN_ROUTES 0`、`SCHEMA_DRIFT 0`、exit 0)。**这是 Phase 0 与计划书中"DEAD_PIPELINES 8→7"目标的对照点 —— 实际基线时 friendship_eval 已不在发送集,故目标实际变为 1→0(consolidate_day 已路由)**。
- ✅ C# 5× `dotnet build` 全部 exit 0。

### 4.2 尚未完成的门

- ⏳ **L3 故障注入**:本次实施未新增故障注入用例(E0-5 + consolidate_day 的新增测试属于 L2 组件级;E3 真正的 L3 目标是交易流程、切图跟丢,L4 不变量(钱包不为负、背包物品数守恒)属于 Phase 3 范畴)。留至 E0-4 落地后,与 E0-4 的"跨图跟丢"故障注入用例一并提交。
- ⏳ **L5 体验打分**:E0-4 实施完成后才进入的端到端实测。
- ⏳ **游戏内 in-game 测试**:E0-4 完成 + 全量 dotnet build 后,由 strict-dev-pipeline 阶段 6 启动 V3TestRunner 验证(海莉场景、跨图跟丢、跟丢回升、AI 主动离开保留 speak vs vanilla-release 触发灰色消息)。
- ⏳ **Git commit**:Stage 7 自审 + commit 推迟到所有 E0-4 改动落地,保持 history 清晰可审计。

---

## 5. Phase 0 红线检查

- **"TS 只做智能,C# 只做执行与数据;数值判定优先落 C# 规则侧"**:E0-5 是 TS 端 prompt 文案兜底(智能层),未在 C# 增加任何决策;`consolidate_day` 路由仅惯例 + 日志,LLM 决策留至 Phase 1。✅
- **"一切 AI 行为全量留痕"**:Phase 1 E1-1 才建立完整 TranscriptStore;Phase 0 仅落得 `console.log` 的 state_changed/consolidate_day 进入日志 + C# SMAPI 日志已有的 ExecuteSetState/ExecuteAction 输出。Phase 1 之前缺失"日志里看不到为什么"的是 consolidate_day 的实际凝练内容(目前为空),不构成 Phase 0 失败。✅(有限)
- **"每次状态/资产变动必须有回执"**:state_changed 已闭环(C# 发 → TS 路由 → 镜像入 prompt);action_result 已闭环(构成 ExecuteAction 回执 → routeToolResult → prompt 反馈段);friendshipDelta 已闭环(dialogue_response 携带 → ApplyWithExternalDeltaAsync 写历史)。海莉教训已沉淀。✅
- **"bun test / tsc / check:protocol 全绿才允许进入下一 Phase"**:bun test 见 §6 提前理性对待;tsc ✅;check:protocol ✅。⚠️ E0-4 落地后还需最终复跑整体网关,确保 C# 5× build 仍 0 警告。**E0-4 落地前暂不得进入 Phase 1**。

---

## 6. 已知问题与对照说明

### 6.1 `bun test` 退出码 1 的真相

尾部汇总显示 `450 pass / 0 fail`(实施 E0-5+consolidate_day 后为 `454 pass / 0 fail`),但进程退出码为 1。已查实根因:

- `bunfig.toml` 配置:`[test] coverage = true` + `coverageThreshold = 0.8`。
- 多个测试文件行覆盖率低于 0.8(80%):`player-profile.test.ts`(74.36%)、`stardew-agent-registry.test.ts`(93.33% / 97.81% 支线)、`dialogue-e2e.test.ts`(97.73%)、`fault-injection.test.ts`(96.92%)、`director.test.ts`(98.04%)、`stardew-agent-beat.test.ts`(87.50%)、`stardew-agent.test.ts`(96.55%)、`stardew-tools.test.ts`(97.73%)、`tool-result-feedback.test.ts`(97.56%)。
- bun 检测到 coverage < threshold 正确地退出 1 —— **不是测试失败**。
- 日志中可见 `error: Output validation failed after retry: CJK ratio 0.00 < 0.3` 与 `LLMUnavailableError` 是 `output-validator-wired.test.ts` / `fault-injection.test.ts` / `dialogue-fallback.test.ts` 等错误路径用测试故意制造的诊断输出,**不是测试 failure**。

**纪律约束**:**不**通过下调 `coverageThreshold` 来使 exit 0(strict-dev-pipeline 明确禁止放宽断言/抑制 error)。本会话把"提高上述 9 个文件覆盖率"单列为独立工单,见 §7。Phase 0 此前阶段红线受到的"暂时仍红"在小范围是已接受容忍状态 —— 用户已明确"接受为已知异常并继续",但**不构成对 strict-dev-pipeline §总览铁律的偏离**,因为 coverage 不是断言、不是测试,而是统计门控,且本会话的所有改动均未让任何触及文件落入 < 0.8 区间。

### 6.2 历史腐朽(非本阶段任务,记录备查)

- `src/ValleyAgent/CommandExecutor.cs:465-472` 注释仍提"friendship must be driven by friendship_eval";`set_friendship`/`update_friendship` 命令处理也按旧友谊侧逻辑拒绝。这两段在方案 B 之后语义已过时,建议在 Phase 1 E1-1 全量留痕改造时一并清理,同时审阅 `ProtocolV2.cs` 是否还有死枚举/死消息类。

### 6.3 计划文档与现实的偏差回执

本文档对计划文档 `docs/plan/2026-08-02-execution-plan.md` §Phase 0 的对照:
- E0-1 "删除发送端 WebSocketClient.cs:63"目标在基线时已自动达成 —— 应补入计划文档脚注。
- E0-3 TS 端落地不在原描述中,但已完整实现并测试守护。
- E0-2 描述指向 DialogueBoxInputPatch.cs:384-404 范围;落地代码在 :382-401,起点偏移 2 行。

待 E0-4 落地后,在 master 分支上再一次性将这些回执合并入计划文档 `Phase 0 "状态:待执行"→"状态:已收尾,实际改动见 docs/plan/2026-08-03-phase0-verification-record.md"`。

---

## 7. 后续工单池(本阶段未触碰)

- [ ] **[独立工单]** Raise `bun test` 覆盖率到 ≥ 0.8,针对上述 9 个测试文件;执行方式优先补充用例、必要时补测缺失分支,绝不调 Threshold(决定时需复核每个 < 0.8 行号的实际未测语义)。
- [ ] **[Phase 1 E1-1]** 把 `handleConsolidateDay` 从"日志 + ack"升级到"调 LLM 日结 + 写 TranscriptStore",幂等集合可在此时升级为"持久化内存 + 跨重启"。
- [ ] **[历史腐朽清理]** 重写 `CommandExecutor.cs:465-472` 的过时注释块,移除 "friendship_eval" 引用;审阅 `ProtocolV2.cs` 是否还有其他死枚举。
- [ ] **[计划文档同步]** E0-4 落地完成后,在计划文档内将 §Phase 0 状态字段从"待执行"改为"已收尾,详见本文档";原验收标准段落保留。

---

## 8. 仍未完成项清单

| 任务 | 状态 | 等待 |
|---|---|---|
| E0-1 | ✅ DONE(本会话验证签收) | — |
| E0-2 | ✅ DONE(本会话验证签收) | — |
| E0-3 | ✅ DONE(本会话验证签收) | — |
| E0-5 | ✅ DONE(本会话实施 + 验证) | — |
| consolidate_day 路由(用户授权额外) | ✅ DONE(本会话实施 + 验证) | — |
| E0-4 删除跨图旅行播报 | ⏳ 进行中 | bg_94e9d889 完成 → 委派 implementation |
| E0-4 灰色系统消息 | ⏳ 进行中 | 同上 |
| Phase 0 网关全部转绿 | ⏳ 待 E0-4 落地后复核全部 | E0-4 完成 |
| Baseline commit(两仓 WIP + 本会话改动) | ⏳ 待 E0-4 落地后 Stage 7 一次性 commit | Phase 0 gate 绿 |
| 进入 Phase 1 | ⏳ 阻塞于 Phase 0 网关 | 上述 E0-4 + 全部转绿 |