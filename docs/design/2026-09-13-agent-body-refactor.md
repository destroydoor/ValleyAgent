# Agent 概念语义重构：从"身份"到"身体"（Issue #9，PR2）

> **日期**：2026-09-13（2026-09-14 审阅修订，见文末修订记录）
> **状态**：已审阅修订，派 subagent 实施；**叠在 PR1 提交 723dadb 之上**（PR1 已实施于 pr1-dialogue-continuity 分支）
> **定案记录**（2026-09-13 与用户确认）：**语义重构**（不删类）＋ **空闲回收** ＋ **spark 保留** ＋ **分两个 PR**
> **关联**：Issue #9、PR1 设计（`2026-09-13-dialogue-continuity-fixes.md`）、三层架构设计 `2026-08-05-three-tier-architecture-redesign.md`

---

## 1. 提案与定性

用户提案（Issue #9）："agent 这个概念我们不是应该移除了吗？所有 NPC 都是能够互动的，主动行为由导演管。"

核实结论：**"是否分配为 Agent"的身份门槛在对话主链路上已经不存在**，剩余门槛只有三处遗留（§2）。因此本设计不是"删除 Agent 概念"的大手术，而是**语义重构**：把分配从"这个 NPC 是不是 AI NPC"的身份判定，改成"这个 NPC 当前是否持有**身体**（状态机/GoalExecutor/执行动作的能力）"的资源池判定。对话不需要分配（谁被聊到谁激活），只有"身体"占资源。

## 2. 事实台账（已验证）

### F1 主机对话已默认全员可 AI 互动

`EnableInfiniteDialogue` 默认 true（Config/ModConfig.cs:506，GMCM「无限对话」）、`NonAgentAIChatEnabled` 默认 true（:111）、`EnableFirstClickVanilla` 默认 true（:226）。
非 Agent 村民对话分支：`NPCDialoguePatch.cs:135-165`——先播原版台词，关闭后经 `CloseDialoguePostfix`（:277-316）自动打开 AI 输入框；`EnableFirstClickVanilla=false` 时直接跳过原版进 AI。

### F2 TS 侧天然全员

`StardewAgentRegistry.getOrCreate`（stardew-agent-registry.ts:85-108）惰性创建 per-NPC 会话（记忆/账本/情绪），对话路径无分配概念。

### F3 动态升级已存在（按需分配的雏形）

`DialogueBoxInputPatch.PromoteToAgent`（DialogueBoxInputPatch.cs:477-540）：非 Agent 村民的回复出现需要"身体"的动作（set_state/follow/干活类，`s_promotionTriggerTools`）时自动 `ForceAllocate`（满员挤最低优先级非 manual Agent，记忆保留在 TS 文件零损失）。

### F4 房客身份门槛（硬门槛 ①，本 PR 拆除）

- 对话 patch：`NPCDialoguePatch.cs:122-124` 房客 isAgent 判定 = `RemoteRenderer.GetRemoteState(name) != null`（只认主机广播名单）；非 Agent 的两条 AI 分支带 `!isThinClient` 门（:142, :155）——房客对非 Agent 村民只能拿原版台词；
- 聊天栏：`ChatBarRouter.BuildPresence`（ChatBarRouter.cs:429-432）房客只把广播名单内 NPC 列为候选。
- 但**主机处理房客对话的中继根本不检查 Agent**（`HostRequestHandlers.HandleDialogueRequest` 直连 provider，HostRequestHandlers.cs:126）——门槛纯在房客侧 UI/patch。

### F5 导演编排已经是按需分配（2026-09-14 审阅修订：原 gap 断言证伪）

TS `morningPlan` 逐 beat 发 `allocate_agent`（protocol-adapter.ts:169-218，KeepUntil=beat 窗口终点）；C# `AllocateAgentHandler`（Protocol/AllocateAgentHandler.cs:29-77）`ForceAllocate + KeepUntil 豁免 + CreateAgent 建身体`。

~~原稿断言"DirectorTools.ResolveAgent 对 beat 窗口外的未分配 NPC 调 set_npc_*/inject_memory 会失败"~~ —— **证伪**（2026-09-14 代码核验）：

- resolver 是 `agentService.TryGetBrain`（DirectorTools.cs:29-31 公共构造器）——**休眠 Brain 也命中**（AgentService.cs:139-148 先查活跃 `_agents` 再查休眠 `_brains`）；
- 存档加载时 `OnSaveLoadedCore → EnsureAllNpcBrainsExist()`（EventHandlerInitializer.cs:913/1017）给全部村民建休眠 Brain，稳态下 ResolveAgent 对任何村民都成功；
- `set_npc_position` 根本不走 resolver（DirectorTools.cs:91-142，直接 `Game1.getCharacterFromName` → `warpCharacter`），无身体也能 warp。

**真正残余 gap = 行为持久性**：无身体 = 无状态机接管原版日程——warp 过去会被原版日程立即拉回，mood/working_on 也无行为表达。数据层（mood/recent_events/working_on/inventory/money/memory）改休眠 Brain 全部有效。B4 据此重定义（§3.3）。

### F6 空闲淘汰机制半现成（2026-09-14 审阅修订）

`AgentAllocationManager` 具备 [Min,Max] 并发池、优先级（对话/送礼/好感度量）、ForceAllocate 挤出、KeepUntil 豁免（2026-08-23 审计扩展到 ForceAllocate/TryAllocate/PromoteToAgent 候选过滤）。**但**：

- `ReevaluateAllocations`（AgentAllocationManager.cs:503）**只在换日被调一次**（EventHandlerInitializer.cs:1093），且只裁超容量（`Count <= MaxAgents` 直接返回），日内无周期调用方；
- 它**没有显式 KeepUntil 检查**——今天安全纯属侥幸：allocate 走 ForceAllocate 必标 manual、排序垫底；
- `OnAgentDeallocated` **无常驻订阅方**（唯一订阅是 PromoteToAgent 的临时捕获，DialogueBoxInputPatch.cs:505-536）——换日裁掉的槽位只是账面释放，无人做 RemoveAgent/日程还原；
- 日内真正的空闲释放是行为级的：`AgentTickLoop.TrackIdleDecision`（Core/AgentTickLoop.cs:128-152）连续 IDLE 达 `MaxConsecutiveIdleBeforeRelease`（默认 3）→ `_releasedToVanilla` 回原版日程，但**不腾池位**。

当前缺三件事：对话结束后 manual override 释放、周期性淘汰驱动、淘汰后的身体拆除接线（B5，§3.4）。

### F7 spark 保留（定案）

`SparkAllocator` 5% 随机预激活仅主机侧（NPCDialoguePatch.cs:127-133 OnSparkCandidate）。与按需分配并存，语义 = 主机侧随机预热的"惊喜感"机制。

## 3. 目标架构

```
对话面：全员可聊（主机已达成 F1；本 PR 拆除房客门槛 F4）
   └─ 房客右键任意村民 / 聊天栏任意在场村民 → 中继主机 → TS getOrCreate → 回复
身体面：按需分配（promote 已有 F3；导演行为类工具按需建身体 F5/B4）
   └─ 需要身体（身体类动作 / set_npc_position / beat）→ 自动 ForceAllocate（受并发上限约束）
回收面：空闲回收（定案）
   └─ 对话结束 / beat 窗口到期 → 身体进入空闲淘汰候选 → ReevaluateAllocations 周期释放
      （KeepUntil 豁免语义不变：beat 有效期内的身体不被挤）
休眠面：保留（= 无身体的默认态）
   └─ 无身体 NPC 走原版路径，零 LLM / 零 tick 开销（性能优先哲学不变）
```

### 3.1 术语与配置

- 文档与注释统一用"身体（Body）"描述分配语义；`AgentAllocationManager` 类名**不改**（避免大手术，测试/事件/DirectorTools/广播全链路零波及）。
- `MaxAgentNpcs` 语义 = **并发身体上限**（数值上限 10 不放宽：>10 身体放大 C# tick 开销）；GMCM 三档（Min/Normal/Max）保留数值行为，文案改为"AI 身体"表述。
- `AgentSyncBroadcaster` 广播名单机制不变，语义变为"当前持有身体的 NPC"；房客**交互不再依赖它过滤**（渲染远程状态仍用）。

### 3.2 房客全员可对话的具体改法

1. `NPCDialoguePatch`：删除非 Agent AI 分支的 `!isThinClient` 门（两处，PR1 后行号约 ：142/:159，以 `EnableInfiniteDialogue` 内容定位）——房客与主机同节奏（EnableFirstClickVanilla 语义一致）；房客 isAgent 判定保留（有身体的 NPC 直接走 Agent 分支）。
2. **`CloseDialoguePostfix` 房客放行（2026-09-14 审阅补充，第三处门）**：`NPCDialoguePatch.cs:306-309` 的 `if (AgentServerProvider == null) return;` 在房客侧恒真——删掉第 1 条的门之后，房客播完原版台词 AI 输入框仍永不弹出（比现状更差的静默无响应）。改为"provider **或** dialogue transport 任一存在即放行"（transport 可达性从 `DialogueBoxInputPatch` 暴露一个 internal 只读判断）。
3. `ChatBarRouter.BuildPresence`：删除房客名单过滤（:429-432），并把其上方"主机侧会拒（TryGenerateDialogue 返回 false）"的**过时注释**一并改写（不实：主机聊天栏与对话框均直连 `GenerateDialogueAsync`，TryGenerateDialogue 不在任何活跃链路上）。
4. 房客请求落主机中继（已支持任意 NPC，F4）；**主机中继的动作分发需对齐 promote 语义**：`ApplyDialogueResponse`（HostRequestHandlers.cs:157-203）当前只对有身体 NPC 记 LastDialoguePlayerId / 执行动作，需复用 `DispatchDialogueActions` 的"身体动作先 promote、失败安全跳过"语义（DialogueBoxInputPatch.cs:413-455），否则房客对无身体村民的回复动作会被静默丢弃。**三个约束**：① 顺序——先 promote 再记 LastDialoguePlayerId，否则首个动作轮的 FOLLOW 目标丢失；② 不得丢 speak/emote 广播——现路径对 speak 也 `ExecuteAction`（广播给其他玩家），而 `DispatchDialogueActions` 会跳过 speak，复用时保留该行为（加开关参数或在中继侧内联 promote 判定，实施时择一）；③ 每次中继对话更新该 NPC 的优先级指标（`UpdatePriority`），保证 B5 的空闲淘汰不会裁掉正在被房客活跃对话的身体。

### 3.3 导演工具行为类按需分配（2026-09-14 审阅重定义）

按"工具是否需要行为接管"分流（与 F5 修订一致），不在 ResolveAgent 里统一分配：

- **行为类**（`set_npc_position`；`spawn_beat`/`spawn_group_beat` 实施时核对——若其实现只写 BeatStore 则归数据类，若有消费活跃状态机的路径则归行为类）：NPC 无身体时先自动建身体（复用 PromoteToAgent 的 ForceAllocate + manual override 释放循环 + KeepUntil 豁免规则），失败原因照旧返回。
- **纯数据类**（`set_npc_mood/recent_events/working_on/inventory/money/inject_memory`）：直接改休眠 Brain（TryGetBrain 已支持），**不建身体**——避免"改个心情就占一个身体名额"。
- 自动建身体的判据：warp 后不被原版日程立即覆盖。

### 3.4 空闲回收接线（定案：空闲回收；2026-09-14 审阅扩充为四步）

1. **对话结束释放 override**：`EndTopicConversation` 释放该身体的 manual override（KeepUntil 未到期不释放，判定模式复用 PromoteToAgent :518-527），身体进入空闲淘汰候选；beat 到期后由 KeepUntil 过期自然纳入。
2. **周期驱动**：在 TimeChanged（每 10 游戏分钟一跳）挂 `ReevaluateAllocations` 调用（现仅换日一次，F6）。
3. **ReevaluateAllocations 显式化两条规则**：① 淘汰候选跳过 KeepUntil 未到期者（今日靠 manual 标记隐式保护，override 释放后必须显式）；② 周期调用时把"KeepUntil 已过期（或从未有）且不在活跃对话中"的 manual override 一并释放——这一条同时兜住**房客中继对话的盲区**（房客关闭对话框主机无感知，EndTopicConversation 不会触发，靠空闲超时释放）。
4. **淘汰拆除接线**：`OnAgentDeallocated` 挂**常驻**订阅 → `RemoveAgent`（降级休眠，Brain/Inventory 保留复用，AgentService.cs:373-389 已支持）+ `controller=null/Halt/恢复日程` 收尾仪式（照抄 PromoteToAgent :551-575 对被挤者的处理）。拆除必须幂等（PromoteToAgent 现有自己的临时捕获+显式 RemoveAgent，两者并存时第二次 RemoveAgent 返回 false 即可，不得重复弹"告别"提示）。

两层"释放"语义并存：行为级（TickLoop `_releasedToVanilla`，已有）+ 池位级（本节新增）。
**不做**"用完即释"（连续对话反复重建、丢 preDialogueState）与"永久保留到换日"（容量裁剪保证池 ≤ MaxAgents，空闲者让位后来者）。

## 4. 成本测算（Issue #9 要求）

| 维度 | 增量 | 依据 |
|---|---|---|
| Token | **≈ 0** | 对话按需（聊到才有 LLM 调用，F2）；beat 每日预算钉死（maxBeatsPerDay=3 + 跨玩家去重 + NPC 冷却）；TS 会话本就 per-NPC 惰性；对话由玩家主动发起，token 可控——多玩家交错带来的 prompt 缓存命中率下降是**已接受的代价**（2026-09-14 用户定案） |
| C# 性能 | **≈ 0** | 身体数量上限不变（MaxAgentNpcs），变的只是"谁在池子里"从预分配变按需；无身体 NPC 零开销（F1 原版路径）；性能承载全部由该容量池完成，**不引入其他池化机制**（2026-09-14 用户定案，无对象池） |
| 同步/协议 | **无变更** | 广播机制照旧（内容语义变化）；无 messages.json 改动 |
| 改动面 | 中型 | 房客三处门 + 中继动作对齐 + 导演行为类按需分配 + 回收接线（override 释放/周期驱动/显式 KeepUntil/常驻拆除）+ GMCM 文案；无删类/无迁移 |

## 5. 明确不做

- 不删 `AgentAllocationManager`/`AgentService` 类（语义重构定案）；
- 不放宽 MaxAgentNpcs 上限（性能）；
- 不动 spark（保留定案）；
- **不动 `NPCGiftPatch.cs:98` 的房客送礼过滤**（2026-09-14 审阅补：主机侧非 Agent 送礼同样回落原版 :109-113，两边本就对称，不是漏网——不要在 B1/B2 时顺手"修"它）；
- **不在本 PR 解决"房客一致性"的完整命题**（2026-09-14 用户定案）：拆除 UI/patch 门槛只是第一步；完整解需要**导演能获取多玩家的游玩上下文**来编排——对应 M3 已知限制的三处缺口：`activity_report` 协议未接线（C# 未上报 per-player 活动）、玩家画像行为层/活动日志仍世界级、导演 game_context 仍是主机世界级快照。列为后续工作；
- 不做 TS 侧任何改动（F2 天然支持）。

## 6. 验证方案

### 自动化门槛

1. `bun test packages/stardew` 回归全绿（TS 零改动，防意外）；`tsc --noEmit`、`check:protocol`；
2. C# 编译 0 警告；xUnit 新增/回归：
   - 房客 patch：非 Agent 村民对话分支在 ThinClient 形态下可达；
   - **CloseDialoguePostfix：ThinClient 形态（provider=null + transport 存在）下原版台词关闭后 AI 框可开（第三处门的回归门）**；
   - ChatBarRouter 候选：房客对任意在场村民入候选；
   - 中继动作：无身体 NPC 的回复动作 → promote 成功后执行 / promote 失败安全跳过；speak 广播行为不回退；
   - DirectorTools：数据类工具对休眠 Brain 直接生效且不建身体；行为类工具（set_npc_position）对无身体 NPC 自动建身体 / 满员失败返回 AgentMissing；
   - 回收：对话结束释放 override + 周期淘汰 → 身体拆除回休眠；KeepUntil 内不释放；ReevaluateAllocations 显式 KeepUntil 跳过。

### Docker 联机 IT（已有 harness）

扩展 C6：房客右键**非 Agent** 村民 → 原版台词 → AI 输入框 → 收到 AI 回复且好感度落账到房客玩家。

### 实机判据（用户验收）

1. 房客与主机行为一致：任意村民都能进 AI 对话；
2. 主机+房客同时聊**不同**的无身体村民，互不阻塞，两个身体按需建立（MaxAgentNpcs 足够时）；
3. MaxAgentNpcs=1 时：最后对话者持有身体；导演 beat 期间身体不被对话挤出（KeepUntil）；
4. beat 窗口外的 `set_npc_position`（TestMod/console 注入）自动建立身体后生效（**判据：NPC 不被原版日程立即拉走**）；纯数据类 `set_npc_mood` 对无身体 NPC 直接生效；
5. 长时间挂机后，空闲身体被回收（console `list-agents` 观察），村民回到原版行为。

## 7. 实施拆分（subagent 任务；叠在 PR1 提交 723dadb 之上）

| # | 任务 | 前置 |
|---|---|---|
| B1 | 房客 patch 三处门移除（两处 `!isThinClient` + CloseDialoguePostfix 放行） | 无 |
| B2 | ChatBarRouter 房客候选过滤移除 + 过时注释改写 | 无 |
| B3 | 主机中继动作分发对齐 promote 语义（ApplyDialogueResponse；含顺序/speak 广播/UpdatePriority 三约束） | 无 |
| B4 | DirectorTools 行为类工具按需建身体（数据类不建） | 无 |
| B5 | 空闲回收四步接线（§3.4） | 无 |
| B6 | GMCM/配置文案 + AGENTS.md 更新 | B1-B5 |
| B7 | xUnit（随 B1-B5 各自交付）+ Docker IT C6（实机阶段） | B1-B3 |

> B1/B2/B3 同属房客链路，同一 subagent 顺序做；B4/B5+B6 第二个 subagent（与第一个串行——B5 的 EndTopicConversation 与 B1 同文件，避免并行冲突）。

---

## 修订记录

### 2026-09-14 审阅修订（主会话代码核验后）

1. **F5 证伪重写**：原"ResolveAgent 对未分配 NPC 会失败"不成立——resolver 是 `TryGetBrain`（休眠命中）+ OnSaveLoaded 全员 EnsureBrain + set_npc_position 不走 resolver。真实 gap = 行为持久性（warp 被原版日程覆盖）。B4 改为"行为类/数据类工具分流"。
2. **B1 补第三处门**：`CloseDialoguePostfix:306` 的 `AgentServerProvider == null` 早退在房客侧恒真，删两处 `!isThinClient` 后原版台词播完 AI 框仍不弹。
3. **F6/B5 扩充**：ReevaluateAllocations 仅换日调用 + 只裁超容量 + 无显式 KeepUntil 检查 + OnAgentDeallocated 无常驻订阅方（拆除只存在于 promote 挤人路径）。B5 定为四步：override 释放 / 周期驱动 / 显式规则 / 常驻拆除，并兜住房客中继对话结束无感知的盲区（空闲超时释放）。
4. **B3 补三约束**：promote 先于 LastDialoguePlayerId；不得丢 speak/emote 广播；中继对话更新优先级指标。
5. **§5 补"不动清单"**：NPCGiftPatch.cs:98 房客送礼过滤与主机对称，不是漏网。
6. 行号校准至 PR1（723dadb）之后：EnableFirstClickVanilla 实际 :226（原 ：228）；`_pendingVanillaDialogueNpc` 置位约 :159（原 :155）。

### 2026-09-14 用户定案补充（成本与边界三条）

1. **Token**：LLM 对话由玩家主动发起，token 可控；多玩家交错导致的 prompt 缓存命中率下降是已接受代价。
2. **性能**：性能承载全部由身体容量池（MaxAgentNpcs ≤10 + 按需分配 + 空闲回收）完成，不引入其他池化机制（无对象池——"池"即既有 `AgentAllocationManager` 并发分配表）。
3. **房客一致性**：完整命题需要导演能获取多玩家游玩上下文（activity_report 接线 / per-player 画像行为层 / per-player game_context），超出本 PR，列后续。
