# NPC 行为反馈闭环与三场景架构改进设计

> 日期：2026-08-01
> 状态：设计稿（不含代码改动）
> 依据：对 `<REPO_ROOT>`（C# 执行层）与 `<VALLEYAI_ROOT>`（TS 智能层）的现状代码核实，而非 AGENTS.md 描述。文中所有 `文件:行号` 均为现状实证。
> 用户硬性要求：
> - **要求一**：NPC 执行操作失败了 NPC 自己必须知道（杜绝"海莉以为自己在跟随"）。
> - **要求二**：prompt 段按变动频率从低到高排列，静态在前动态在后，维持前缀缓存高命中率。
> - **补充约束**：NPC 旅行中**可以说话**（"我马上就到"在真实赶路时是合时宜的），要修的是"说了但没在做"，不是禁言。

---

## 0. 现状核实：必须先知道的地雷

以下问题在三个场景的改进中都绕不开，列为前置事实。

### 0.1 死管道（最严重）

C# 仍在发送 TS 服务器**已不路由**的消息类型。TS `routeMessage`（`protocol-adapter.ts:145-155`）只处理 5 种消息（hello/ping/dialogue/tool_call_result/action_result），未知类型回 `ack` 且 `requestId:"unknown"` 被丢弃：

| C# 发送的消息 | 发送位置 | 后果 |
|---|---|---|
| `decision` | `WebSocketClient.cs:45`，由 `EventHandlerInitializer.cs:1713` 周期触发 | **定时 LLM 决策管线整体是死的**。`PendingRequestTracker` 按 requestId 匹配永远等不到响应 → 60s 超时 → 熔断 → NPC 长期只靠 `RuleBasedDecisionEngine` 驱动 |
| `friendship_eval` | `WebSocketClient.cs:63`，由 `FriendshipSystem.cs:122` 调用 | 对话好感度评估静默失败；且 `FriendshipSystem.cs:126-137` 的 catch 不含 `TimeoutException`，超时异常向上穿透，每次超时占住信号量 60s 串行化后续所有评估 |
| `gift_eval` / `state_sync` | 同上 | 同上，依赖 state_sync 响应命令的 `SetStateCommand`/`GiveGiftCommand` 全部是死代码 |

**任何场景改进之前，必须先决定：把这些消息类型在 TS 端接回去，还是把 C# 侧发送方删掉。** 现状是两端各自以为对方在工作。

### 0.2 乐观执行、无对账

TS 端工具在 agentLoop 内**立即执行"记忆/账本"语义**（`stardew-agent.ts` → `agent-loop.ts:100-136`），同时把动作收集进 `actions[]` 交给 C# 执行"游戏世界"语义。两端**没有对账**：

- `give_gift` 在 server 端已扣库存 + 写记忆（importance=6），即使 C# 侧执行失败也不回滚 → 记忆与现实分叉。
- C# 侧 `give_item`/`give_gift` **永远报成功**：物品不存在、NPC 不存在都是静默 return，`success=true`（`CommandExecutor.cs:173-208`）。
- 对话路径的 `set_state` 被 `DialogueBoxInputPatch.cs:384-404` 拦截走 `TrySetAgentState`，**返回值被丢弃且 `continue` 跳过反馈**——状态机拒绝（agent 不存在/非法状态/BLOCKED）时 TS 永远以为成功。**这就是"海莉事件"的直接根因。**

### 0.3 反馈环的蒸发点

TS 端反馈队列机制本身是对的（失败结果会注入下轮 prompt + 写短期记忆，`stardew-agent.ts:330-346`），但有两个蒸发点：

1. `drainToolResults` 在 LLM 调用**之前**清空队列（`protocol-adapter.ts:102-107`）；LLM 失败走 fallback 时，本批反馈既没注入也没落记忆，**直接蒸发**。
2. 成功的结果**不写记忆**，只在下一轮 prompt 出现一次。`set_state` 成功这件事之后无迹可查（`set_state` 工具写的是"决定切换到 X"的**意图**记忆，不是"已经在 X"的**现实**记忆）。

### 0.4 功能分裂的双路径

多处存在"完整实现挂在死路径上，活路径是残缺版"：

| 功能 | 完整实现（死路径） | 残缺实现（活路径） |
|---|---|---|
| 对话 | `DialogueManagementApi.TryGenerateDialogue`：写 C# 对话记忆、好感度评估、emotion 同步——**生产代码无任何调用方** | `DialogueBoxInputPatch.SubmitInput`：不写 C# 侧记忆、不评估好感度、不消费 `response.Emotion` |
| NPC 送礼 | `GiveGiftCommand`（`Commands/ItemCommands.cs:64-140`）：校验背包、扣减、写记忆、情绪、仪式感——挂在死掉的 state_sync 路径 | `CommandExecutor.ExecuteGiveItem`：凭空造物、不扣库存、不写记忆、不报失败 |

### 0.5 其他已核实的硬编码/断点

- `memorySideEffect` 硬编码 `"recorded"`（`protocol-adapter.ts:123`）；`emotion` 硬编码 `"Neutral"`（`stardew-agent.ts:420`）。
- `get_info health` 返回假数据 `"100/100"`（`stardew-tools.ts:224`）。
- `LLMBillingError` 是死代码（provider 不识别 402/429），rule-engine 三档人设兜底实际只有两档可达。
- fallback 路径不调 `memory.save()`：玩家输入已进 `conversationHistory` 但进程重启即丢；NPC 兜底台词不写对话历史 → 对话记录里玩家说了话却没有下文。
- `WebSocketClient.SendMessageAsync` 未连接时**静默丢弃**（`WebSocketClient.cs:374` 注释明说 silently drop）——`action_result`、`state_changed` 这类关键反馈在断线期无声丢失。
- 玩家切图导致 FOLLOW 被动解除时，`OnStateChanged` 按"任务完成" +2 好感（`EventHandlerInitializer.cs:2022-2060`）。

---

## 1. 场景一：对话 → NPC 同意跟随 / 帮忙挖矿砍树

### 1.1 玩家体验目标（验收标准）

1. 玩家说"跟我走"，NPC 同意 → NPC **真的**进入跟随。
2. 跟随不突然丢失；**只有三种合法退出**：LLM 主动决策退出、玩家明确结束、紧急情况（血量/怪物贴脸）。
3. 跨地图跟随：玩家进新图，NPC 从玩家进入该图的入口位置出现并继续跟；连续快速切图也能跟。
4. 旅行中可以说话——"我马上就到"在 NPC **确实在赶路**时是好的沉浸感；问题是它说了却没在赶路。
5. 干活类请求（挖矿/砍树/收菜）同理：答应了就真的进入对应状态并执行。

### 1.2 现状下的失败模式（逐个对应体验事故）

| # | 失败模式 | 实证位置 | 玩家看到的现象 |
|---|---|---|---|
| F1 | LLM 调 `set_state(FOLLOW)`，C# 状态机拒绝，返回值被丢弃，无 `action_result` 回发 | `DialogueBoxInputPatch.cs:384-404`；`AgentActionApi.cs:62-77` 只写日志 | **海莉事件**：NPC 说"好，我跟你走"，然后原地站桩；玩家质问时 NPC 以为自己在跟 |
| F2 | FOLLOW 中玩家快速连续切图，旅行重启失败（无法路由） | `AgentNavigator.cs:147-150, 714-717` 仅 Warn 日志 | NPC 停在旧图，状态机仍是 FOLLOW，玩家以为 NPC 跟丢了是 bug |
| F3 | 旅行到达时 `warpCharacter` 抛异常 | `AgentNavigator.cs:649-681`：catch 只记日志，随后照常 `EndTravel`；`ShowNpc`（`:774`）只移出隐藏集合**不恢复位置** | **NPC 永久隐形**（留在 (-1000,-1000)），状态仍是 FOLLOW，每 tick 重新发起旅行再失败，死循环。玩家和 TS 都收不到任何信号 |
| F4 | 旅行途中 `CancelTravel` 恢复失败 | `AgentNavigator.cs:210-214`：注释说"交给 DayStarted 兜底" | NPC 在 (-1000,-1000) 等一整天才被日程安置 |
| F5 | 满员时对话升级 `PromoteToAgent` 淘汰旧 Agent | `DialogueBoxInputPatch.cs:498-511`：被淘汰者直接 `controller=null; followSchedule=true` | 另一个 NPC 无声无息停止跟随，无任何通知 |
| F6 | 玩家请求**砍树**：工具集里根本没有 chop | `stardew-tools.ts` 9 个工具无砍树；状态枚举也无 CHOP | LLM 只能用语言搪塞，或调一个不存在的工具被校验拦下，玩家觉得 NPC 只会嘴炮 |
| F7 | FOLLOW 被紧急状态/切图顶掉后 | `EventHandlerInitializer.cs:1614-1640, 1890-1921` 无通知 | 同 F1 的"以为还在跟" |
| F8 | 旅行中 NPC 说话 | Travelling 阶段 NPC 被隐藏在 (-1000,-1000)（`AgentNavigator.cs:761-768`） | 气泡在虚空位置渲染不可见；speak 的内容玩家看不到 |

### 1.3 架构改进：意图/现实分离 + 强制回执

核心原则：**TS 管"意图与心智"，C# 管"身体与现实"，现实状态的唯一事实源是 C# 状态机，且一切变化必须主动上报。**

#### (a) ActionLedger：一切动作强制回执

- **协议**：`actions[]` 中每个 action 必须回 `action_result`，不允许静默。`success=false` 时携带机器可读 `reason` 枚举：
  `AGENT_MISSING` / `TRANSITION_BLOCKED` / `INVALID_STATE` / `TARGET_UNREACHABLE` / `ITEM_NOT_FOUND` / `INVENTORY_FULL` / `LOCATION_INVALID` / `INTERNAL_ERROR`。
- **C# 改动点**：`DialogueBoxInputPatch.DispatchDialogueActions` 不再丢弃 `TrySetAgentState` 返回值、不再 `continue` 跳过反馈；`CommandExecutor` 各 Execute* 方法消灭"静默 return + 报成功"。
- **TS 改动点**：`routeToolResult` 无 `npcName` 不再静默丢弃（记入全局错误日志 + 指标）。
- **断线保护**：`WebSocketClient` 未连接时 `action_result` 不再 silently drop，改入本地持久化 outbox（JSONL 追加），重连后按序补发。反馈丢失是"NPC 失忆"的直接来源，不能省。

#### (b) 意图/现实双轨记忆

- **意图记忆**：工具调用即写（现状已有，`set_state` 写"我决定跟随"）。
- **现实状态镜像**：TS 端 per-NPC 维护 `actualState`，**只能由 C# 推送更新**，LLM 工具不能写。
- **新消息 `state_changed`**（C# → TS，需加入 TS `routeMessage` 路由）：
  ```
  { type: "state_changed", npcName, from, to, reason, reasonDetail?, atTick }
  ```
  `reason` 枚举：`llm_decision` / `player_request` / `emergency` / `evicted` / `travel_failed` / `task_completed` / `handler_exit` / `day_started` / `vanilla_release`。
- **触发点**：C# 状态机的**每一次**转换（含被动解除、淘汰、旅行失败转 IDLE）都发。这是要求一的根基——NPC "知道"的代价就是这一条消息。
- **写入**：TS 收到后做三件事——更新 `actualState`；若与意图不一致，写一条第一人称短期记忆（"我想跟着他，但是我跟丢了/我被日程叫走了"，importance=6）；若玩家在线可见，可选触发一句解释性台词（见 (d)）。
- **prompt 注入**：系统 prompt 增加"─── 我现在的真实状态 ───"段（位置见 §4），内容如 `正在跟随农场主（同图）` / `正在赶往农场的路上` / `空闲（上次跟随失败：目标地图不可达）`。LLM 回答"你为什么不跟着我"时以这段为准。

#### (c) FOLLOW 生命周期契约与旅行失败恢复

- **合法退出白名单**：`llm_decision`、`player_request`、`emergency`。其余一切退出路径（淘汰、旅行失败、切图顶掉）在转换前必须发 `state_changed`，**并视为异常事件**计入指标。
- **F3 修复（设计）**：`warpCharacter` 失败 → 回滚到旅行发起前的位置并 `ShowNpc`（真正恢复 Position，不是只移出集合）→ `ForceTransition(IDLE)` → 发 `state_changed(reason=travel_failed)` → 玩家可见一句气泡/聊天（"我好像过不去了…"）。禁止留在 (-1000,-1000)。
- **F2 修复（设计）**：旅行重启失败同上：恢复显示 + 转 IDLE + 上报，不再停在旧图保持 FOLLOW 假象。
- **连续失败熔断**：同一 NPC 旅行连续失败 ≥2 次，本次游戏日内不再自动发起跨图旅行，等 LLM 显式决策（避免死循环刷日志）。
- **F5 修复（设计）**：淘汰旧 Agent 前发 `state_changed(reason=evicted)`，并向玩家发一条聊天消息（"Haley 停下脚步，回去忙自己的事了"）。淘汰是玩家可感知事件，不该无声。

#### (d) 旅行中可以说话（用户补充约束的落地）

- **不禁言**：Travelling / Departing 阶段 speak、show_dialogue 工具保持可用。
- **上下文知情**：prompt 的真实状态段写明"我正在赶往 {地图名} 的路上"，NPC 说"我马上就到"时它是**真的**在路上——这句话从"不合时宜"变成沉浸感来源。
- **渲染路由**：Travelling 中 NPC 被隐藏，气泡不可见 → C# 侧 speak 执行器检测 `AgentNavigator.IsTravelling(npc)`，旅行中的发言自动改走 `Game1.chatBox`（带 NPC 名字颜色），或在到达 `ShowNpc` 后补一个头顶气泡。二选一即可，推荐聊天框（不打断到达动作）。
- **频率软约束**：旅行中说话走正常 speak 路径即可，不必额外限流；LLM 有真实状态上下文后自然不会每 2 秒喊一次。

#### (e) 砍树能力补全（F6）

- 短期：新增 `chop_tree` 工具 + `CHOP` 状态（或泛化为 `set_state(WOODCUT)`），Handler 复用 MineHandler 的模式：找树/树桩 → 走位 → 挥斧动画 → 掉落物入背包 → 收尾仪式。
- 长期建议：**任务型状态泛化**。FARM/MINE/FORAGE/CHOP 结构同构（找目标→接近→执行→收尾→退出），抽一个 `GatherTaskHandler` 基类，目标源和执行动作参数化。新增干活类型不再复制整套 Handler。
- 对话承诺校验：prompt 规则段补一条"只能承诺你有对应工具能做的事"——工具列表就在 system prompt 里，LLM 天然受约束。

### 1.4 海莉事件走查（改进后）

1. 玩家："海莉，跟我去矿洞。" → LLM 调 `speak("好呀")` + `set_state(FOLLOW)`。
2. 假设 C# 拒绝（agent 未分配）→ 回 `action_result{success:false, reason:AGENT_MISSING}` → TS 注入下轮 prompt"上次行动结果：set_state 失败" + 写记忆"我想跟他走但没走成"。玩家再问时海莉会说"我好像没办法离开这里…"而不是以为自己在跟。
3. 假设成功 → 玩家进矿洞 → `OnPlayerWarped` 触发跨图旅行 → 真实状态段变为"正在赶往矿洞的路上" → 海莉此时说"我马上就到！"（聊天框显示）——**合时宜**。
4. 假设矿洞 warp 失败 → 回滚显示在原地 + `state_changed(FOLLOW→IDLE, travel_failed)` + 海莉气泡"呼…那边我好像进不去"。玩家问"你怎么没跟来"，海莉的记忆里有正确答案。

---

## 2. 场景二：双向送礼

### 2.1 玩家 → NPC

**现状可用部分**：`NPCGiftPatch` Prefix 的礼物评估、好感度（递减收益+生日倍率）、记忆、情绪都在本地完成，断连也工作（`NPCGiftPatch.cs:184-269`）——这条链路是对的，保留。

**失败模式**：

| # | 失败模式 | 实证 |
|---|---|---|
| G1 | LLM 反应台词连续失败 ≥2 次后，当天不再请求 → 礼物被吃、好感已变、**NPC 完全沉默** | `NPCGiftPatch.cs:318-322` |
| G2 | 情绪双来源互相覆盖：Prefix 用 `EmotionAnalyzer` 推导，Postfix 硬编码映射再 `SyncEmotion` 一次 | `NPCGiftPatch.cs:424-432` |
| G3 | 非 Agent NPC 送礼直接交原版，ValleyAgent 不记任何事 | `NPCGiftPatch.cs:88-91`——与"非 Agent 也能 AI 对话"不对称 |

**改进**：

- **G1 → 反应保证**：LLM 不可用时降级为**本地规则反应**（按 giftTaste 四档预制人设化短句，从 npc_prompts 的语气生成模板或手写兜底），保证"送礼必有反应"。LLM 台词是增强，不是唯一路径。
- **G2 → 情绪单一来源**：删掉 Postfix 硬编码映射，统一走 `EmotionAnalyzer`；LLM 返回的 emotion 字段若启用（当前硬编码 Neutral，见 §0.5）只能作为"建议"，落地仍经 Analyzer。
- **G3 → 非 Agent 也记**：玩家送非 Agent 村民礼物时，写一条该 NPC 的 server 端记忆（send 一条轻量 `gift_notice` 消息或并入下次 dialogue 的 worldSnapshot），保持"全镇 NPC 都有记忆"的对称性。

### 2.2 NPC → 玩家

**失败模式**：

| # | 失败模式 | 实证 |
|---|---|---|
| G4 | 凭空造物：不检查不扣减 C# 侧 `AgentInventory`；TS 端却已乐观扣库存写记忆 → 两端账本分叉 | `CommandExecutor.cs:173-208` vs `stardew-tools.ts:85-113` |
| G5 | 永远报成功（物品不存在也 success=true） | `CommandExecutor.cs:94-99, 173-208` |
| G6 | 不写记忆、不触发 `GiftGiven` 情绪（完整实现 `GiveGiftCommand` 挂在死路径上） | §0.4 |
| G7 | 玩家背包满 → 物品掉 NPC 脚下，仅 Debug 日志，玩家可能根本没看见 | `CommandExecutor.cs:194-201` |

**改进**：

- **统一实现**：合并双路径，对话路径的 give_gift 调用与 `GiveGiftCommand` 同一套逻辑（校验→扣减→记忆→情绪→仪式感→回执）。顺手清掉 state_sync 死代码。
- **两阶段账本（对账）**：TS 端 `give_gift` 不再立即扣库存写记忆，改为写**意图**（"我想送他一朵花"）；C# 执行后回 `action_result`：
  - success → TS 落"现实"：扣库存 + 写送礼记忆 + `GiftGiven` 情绪；
  - failure(reason=ITEM_NOT_FOUND/INVENTORY_FULL) → TS 写失败记忆，LLM 下轮自然解释"我翻遍口袋也没找到…"。
  speak 类无对账问题的工具保持当轮执行。
- **G7 → 玩家可见**：掉地上时发聊天消息（"Haley 把 XX 放在了你脚边"），并给物品 debris 加一个短暂高亮/箭头（可选）。

---

## 3. 场景三：晨间叙事引擎

### 3.1 现状：代码齐了，线没接

TS 端已有一整套 **Narrative Director**（休眠代码）：

- `director.ts`（478 行）：`morningPlan()` 每日 6am 生成 0–3 个 beat、`milestoneReact()`；`react-guard.ts` 软刹车。
- `narrative-types.ts`：8 种新 WS 消息类型——但 `server.ts` 不 import Director，`routeMessage` 不路由，8 种消息全部落入 default 分支丢弃。
- `prompt-builder.ts` 的 `BEAT_SYSTEM_TEMPLATE` + `StardewAgent.runBeat`（`stardew-agent.ts:208-301`，maxTurns=8，终止条件要求 speak + 至少一个其他工具）。
- `activity-log-store.ts`（SQLite）、`beat-store.ts`、`player-profile*.ts`、`game-context.ts`。

C# 侧的"晨间触发"目前依赖死掉的 `decision` 消息（§0.1）。

### 3.2 目标体验拆解

- 玩家起床 → 今天"有事在发生"：NPC 有自己的安排，可能主动来找玩家、托玩家办事、或只是过自己的生活并被玩家撞见。
- NPC 行为**符合星露谷常识**：雨天不浇地、节日去广场、冬季不种作物、周二皮埃尔关门……
- NPC 行为**符合人设与关系**：Sam 邀你滑滑板而不是讨论矿石；好感低的 NPC 不会大清早堵你门口。
- beat 执行失败了 NPC 知道（要求一贯穿）。

### 3.3 晨间流程设计

```
C# DayStarted
  → 收集 MorningContext：日期/季节/天气/节日与生日日历/每个候选 NPC 的位置与日程知识/
    昨日事件摘要（action_result outbox + state_changed 流水）/当前 Agent 分配与空位
  → 发送新消息 day_started（加入 TS 路由）
TS Director.morningPlan(MorningContext)
  → 筛今日主角 NPC（0–3 个 beat，含"无 beat"为合法结果——NPC 大多数时候过普通日子）
  → 每个 beat：{ npc, goal, 约束(地点/时间窗/需要玩家在场?), 人设 prompt 引用 }
  → 回复 C#：beat_plan 消息
C# AgentAllocationManager
  → 叙事驱动分配：beat 主角优先占用 Agent 槽位（高于对话 PromoteToAgent 的临时升级），
    被淘汰者走 §1.3(c) 的有通知淘汰
  → 到时间窗 → 发送 beat_start(npc, beat) → TS runBeat → actions[] → C# 执行 → action_result 回执
  → beat 结束（完成/失败/超时）→ beat_end → TS 记录 beat-store + 写 NPC 记忆
```

### 3.4 "叙事引擎真的懂星露谷"——上下文分层注入

Director 的 prompt 按 §4 的同一条静态→动态规则组织，知识分三层：

| 层 | 内容 | 数据来源 | 变动频率 |
|---|---|---|---|
| 世界规则（静态） | 星露谷季节/作物/天气规则、商店营业、节日日历、地图连通性 | 内置知识文件（新增 `stardew_world.json`，可由 RAG 知识库导出） | 不变 |
| 角色档案（准静态） | 每个 NPC 的人设摘要、原版日程规律、喜好、关系网 | `npc_prompts.json` + `RAGKnowledgeBase` 导出 | 随好感阶段变 |
| 今日情境（动态） | 日期/天气/节日、昨日事件、玩家近况、各 NPC 记忆摘要 | MorningContext | 每天变 |

"懂角色"靠 beat 执行时的 runBeat prompt：该 NPC 的阶段 prompt + significant memories + 真实状态段（§1.3(b)）+ beat goal。Director 只做"谁今天有戏"，不做台词。

### 3.5 "NPC 真的有自己的生活"

- **无 beat 是常态**：react-guard 软刹车保留；大多数日子大多数 NPC 由原版日程 + `IdleWanderHandler` 生活化，叙事引擎只在"有戏"时介入。玩家撞见 NPC 在按自己日程干活，本身就是生活感。
- **beat 结果沉淀**：完成/失败都写入该 NPC 记忆（失败同样写——"我想约他去钓鱼，但下雨了"），成为后续对话素材，叙事产生长尾效应。
- **失败对玩家可见但不出戏**：beat 动作失败 → NPC 用第一人称向在场玩家解释或自嘲，而不是卡住/复读。

### 3.6 失败模式与对策

| # | 失败模式 | 对策 |
|---|---|---|
| N1 | Director LLM 失败/超时 | 当天无 beat，NPC 回归原版日程——**降级是隐形的**，玩家无感，优于错误叙事 |
| N2 | beat 主角 Agent 槽位抢不到 | beat 标记 deferred 进入时间窗等待；窗口过期标记 failed 写入记忆（"本来想去找他，结果没赶上"） |
| N3 | beat 动作（move_to/give 等）执行失败 | 走 §1.3(a) ActionLedger 统一回执；失败写入记忆并可选解释台词 |
| N4 | 玩家当天行为打乱 beat（比如提前下矿） | beat 执行前检查前提（玩家位置/在场），不满足则 deferred；不把 NPC 硬拽到玩家面前 |
| N5 | 叙事与原版日程冲突 | beat 拥有 Agent 期间 `followSchedule=false` 照旧；beat 结束交还日程 |

---

## 4. 要求二落地：prompt 段重排（双端统一规则）

### 4.1 规则

**一切 prompt（对话、beat、Director、未来的决策）的段落按变动频率单调递增排列：静态 → 准静态 → 低频动态 → 高频动态 → 当轮输入。静态段永远不出现在动态段之后。**

### 4.2 对话 prompt 现状（实测顺序，`prompt-builder.ts:7-42`）

| # | 段 | 变动频率 | 问题 |
|---|---|---|---|
| 1 | 规则 | 静态 | ✅ |
| 2 | 我是谁（人设+阶段+base_memory） | 准静态 | ✅ |
| 3 | 我永远不会忘记的事（significant） | 低频，但**每加一条断掉其后全部缓存** | 位置可接受但需靠后 |
| 4 | 最近对话（10 条） | **每轮必变** | 🔴 太靠前，其后全灭 |
| 5 | 上次行动结果 | 偶发 | 🔴 |
| 6 | 当前场景 | **每轮必变** | 🔴 |
| 7 | 最近记忆 | 高频 | 🔴 |
| 8 | 重要事项记忆规则 | **静态** | 🔴 静态段排在最后，永远吃不到缓存 |

当前前缀缓存最多覆盖到第 2 段（约 15% 的 token）。

### 4.3 目标结构

| # | 段 | 变动频率 |
|---|---|---|
| 1 | 规则 + 工具使用约束 | 静态 |
| 2 | 重要事项记忆规则（原第 8 段前移） | 静态 |
| 3 | 我是谁（人设+阶段 prompt+attitude brief+base_memory） | 准静态（好感跨档才变） |
| 4 | 我永远不会忘记的事（significant memories） | 低频追加 |
| 5 | 我现在的真实状态（§1.3(b)，含旅行中/跟随对象/位置） | 中频（状态变化才变） |
| 6 | 最近记忆（短期记忆摘要） | 高频 |
| 7 | 最近对话（窗口收缩到 6–8 条，控制长度） | 每轮必变 |
| 8 | 当前场景（季节/时间/天气/位置/nearby） | 每轮必变 |
| 9 | 上次行动结果（仅非空出现，放最末——它是"刚发生的事"，语义上也属于最新上下文） | 偶发 |
| — | user：农场主说：{input} | — |

预期可命中前缀从 ~15% 提升到 60%+（第 1–4 段在大多数轮次完全不变；第 5 段状态不变时仍可命中）。

### 4.4 配套细则

- **significant memories 追加会断其后缓存**：这是正确语义的代价，无法避免；把它放在所有动态段之前，使损失仅限于动态段（反正动态段本来就 miss）。
- ** Director / beat prompt 同规则**：`BEAT_SYSTEM_TEMPLATE` 按 4.3 同样排序；世界规则/角色档案（§3.4 的静态两层）永远在最前。
- **数值稳定化**：时间段用游戏内离散档位（morning/afternoon/...）而非分钟数；nearby 列表排序稳定（按类型+名字），避免同等内容因顺序抖动产生不同前缀。
- **防回归**：加一个 TS 单元测试——对 `DIALOGUE_SYSTEM_TEMPLATE` 的段落顺序做断言（静态段索引 < 动态段索引），防止后人把静态段又加到尾部。

---

## 5. 实施顺序建议（纯设计，供排期参考）

| 阶段 | 内容 | 解什么痛 |
|---|---|---|
| P0 | 协议对齐：决定 `decision`/`friendship_eval`/`state_sync` 接回或删除；`state_changed` 新消息两端落地；action_result 强制回执 + reason 枚举；C# 消灭静默 return | 死管道、海莉事件（F1/G5） |
| P0 | 意图/现实双轨记忆 + prompt 真实状态段 | 要求一 |
| P0 | prompt 段重排 + 顺序断言测试 | 要求二 |
| P1 | FOLLOW 生命周期契约：旅行失败回滚、淘汰通知、连续失败熔断；旅行中发言走聊天框 | F2–F5、F8 |
| P1 | 送礼双路径合并 + 两阶段账本 + 反应保证（本地兜底台词） | G1–G7 |
| P2 | chop_tree / 任务型状态泛化 | F6 |
| P2 | Director 接线：day_started / beat_plan / beat_start / beat_end 路由 + 叙事驱动分配 | 场景三 |
| P3 | outbox 断线补发、情绪单一来源、`memorySideEffect`/`emotion` 去硬编码 | §0.5 尾巴 |

---

## 6. 测试与打分

见配套文档 `docs/design/2026-08-01-test-scoring-redesign.md`。本设计中的每个"失败模式"编号（F1–F8 / G1–G7 / N1–N5）在测试文档中都有对应的故障注入用例与不变量断言。
