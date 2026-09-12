# FINDINGS.md 修复方案设计文档

> **创建时间**: 2026-07-18
> **状态**: 设计阶段（待用户审核）
> **修复范围**: P0-P3 全量
> **实施顺序**: P0 先交付 → P3 架构 → P1/P2
> **代码位置**: `D:\Source\ValleyAI`（TS 服务器）+ `D:\Source\ValleyTalk`（C# Mod）
> **原始报告**: `D:\Source\ValleyTalk\test-recordings\kimi_player_eval\FINDINGS.md`

---

## 1. 决策摘要

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 修复范围 | P0-P3 全量 | 用户要求一次性解决所有问题 |
| 实施顺序 | P0 → P3 → P1/P2 | P0 先止血，P3 重构执行末端，P1/P2 在干净架构上重写 |
| 服务器实现 | 完全用 TS，废弃 Python | 用户明确指示；ValleyAI core 层已就绪 |
| B1 对话管线 | 重写 `_handle_dialogue` 为有状态路径 | 语义清晰，对话与决策分离 |
| Action 机制 | 混合（tool_call 优先 + 文本兼容） | 结构化为主，文本标签降级 |
| IAgentEntity 边界 | 中集（位置/朝向/说话/emote/动画 + 移动子系统） | 收敛 IMovementService 7 处耦合 |
| P3 实体策略 | 双实体（隐藏 NPC + Farmer 外壳） | 一次性解决联机 E1-E4 + D4 远端冻结 |
| 上下文管理 | C# 把全部上下文传给 TS，Agent 框架管理 | C# 是纯执行器，零状态零解析 |

---

## 2. 整体架构

### 2.1 三层架构

```
┌──────────────────────────────────────────────────────────────┐
│  C# Mod (ValleyAgent)  —  纯执行器                            │
│  ├─ 输入：玩家点击/输入 → 发 raw context 到 TS                 │
│  ├─ 输出：收 {speech, actions[]} → 直接渲染/执行               │
│  ├─ ServerProcessManager: 启停 Bun exe                        │
│  └─ WebSocketClient: 协议层，无业务逻辑                       │
├──────────────────────────────────────────────────────────────┤
│  @valley/stardew  —  星露谷特化层                              │
│  ├─ StardewAgent: 组装 core 原语 + 星露谷配置                  │
│  ├─ ProtocolAdapter: 18 消息类型 ↔ core 通用接口              │
│  ├─ PromptBuilder: npc_prompts.json 加载 + 阶段注入           │
│  ├─ Tools: 20 个工具（speak/emote/give_item/remember/...）    │
│  ├─ Memory: AgentMemory（持久化到 agents/<npc>_memory.json）  │
│  ├─ FSM: 7 状态 + 转换表                                       │
│  ├─ RAG: JSON 关键词检索                                       │
│  └─ RuleEngine/OutputValidator                                 │
├──────────────────────────────────────────────────────────────┤
│  @valley/core  —  通用 Agent 执行框架（已有）                  │
│  ├─ Agent + agentLoop（双队列/工具调用/hooks）                 │
│  ├─ ToolRegistry + Tool（可见性三级）                          │
│  ├─ LLMProvider（Vercel SDK + direct-fetch fallback）         │
│  ├─ Transport（Bun WebSocket）                                 │
│  └─ CircuitBreaker/TokenBudget/PerformanceMonitor             │
└──────────────────────────────────────────────────────────────┘
```

### 2.2 核心数据契约

**C# → TS（dialogue 请求）**：
```json
{
  "type": "dialogue",
  "requestId": "<uuid>",
  "npcName": "Haley",
  "playerInput": "你今天看起来心情不错",
  "worldSnapshot": {
    "season": "summer", "day": 28, "time": "14:30", "weather": "sunny",
    "location": "Town", "npcTile": {"x": 32, "y": 18},
    "nearbyObjects": "2 villagers, Pierre's shop entrance",
    "friendship": 250, "npcState": "IDLE",
    "inventory": [{"name": "Amethyst", "quantity": 2}],
    "farmerName": "新来的农夫"
  }
}
```

**TS → C#（dialogue 响应）**：
```json
{
  "type": "dialogue_response",
  "requestId": "<uuid>",
  "npcName": "Haley",
  "speech": "嗯？你跟我说话？算你有眼光。",
  "actions": [
    {"tool": "emote", "args": {"emote_id": "heart"}},
    {"tool": "set_state", "args": {"state": "TALK"}}
  ],
  "emotion": "Neutral",
  "memorySideEffect": "recorded"
}
```

要点：
- `speech` 是 NPC **说了什么**（来自 speak/show_dialogue 工具调用的 text 参数）
- `actions` 是 NPC **做了什么**（除 speak 外的所有工具调用，结构化传给 C# 执行）
- `response.Text` 字段消失，改为 `response.Speech`（语义清晰）
- `response.Action` 字符串消失，改为 `response.Actions[]` 数组
- LLM 原始文本不传给 C#（C# 不再解析 `[ACTION:]` 标签）

---

## 3. 组件清单与边界

### 3.1 @valley/stardew 模块清单

| 模块 | 职责 | P0 实现 | P3 实现 | 来源 |
|------|------|---------|---------|------|
| **StardewAgent** | 组装 core 原语 + 星露谷配置，每 NPC 一个实例 | ✅ 对话最小集 | ✅ 完整 | 新建，复用 e2e/stardew-run.ts 雏形 |
| **ProtocolAdapter** | 18 消息类型 ↔ core 接口桥接 | ✅ 5 消息（hello/ping/dialogue/tool_call_result/action_result） | ✅ 18 消息全量 | 新建 |
| **PromptBuilder** | npc_prompts.json 加载 + 阶段注入 + 场景填槽 | ✅ 对话专用 | ✅ 决策/评估复用 | 复用 e2e/stardew-prompt.ts |
| **Tools** | 20 个工具定义 | ✅ 8 个对话工具（speak/emote/give_item/give_gift/set_state/show_dialogue/remember/get_info） | ✅ +12 个决策工具 | 复用 e2e/stardew-tools.ts |
| **AgentMemory** | 短期/长期/重要记忆 + 对话历史 + 持久化 | ✅ JSON 文件持久化 | ✅ +3 层衰减 | 复用 e2e/stardew-memory.ts，加 load/save |
| **NpcPromptLoader** | 33 NPC × 5 阶段数据加载 | ✅ 加载 Haley 等关键 NPC | ✅ 全量 33 NPC | 新建，读 `data/npc_prompts.json` |
| **WorldSnapshotDecoder** | 解析 C# 发来的 worldSnapshot 到 SceneState | ✅ | ✅ | 新建 |
| **FSM** | 7 状态 + 转换 + 最小持续时间 | ❌ P0 不需要（对话不切状态） | ✅ | 新建 |
| **RAGEngine** | JSON 关键词检索（NPC/物品/地点/节日/机制） | ❌ P0 不需要 | ✅ | 复制 Python rag_index.py 逻辑 |
| **RuleEngine** | LLM 失败时的本地决策 fallback | ❌ P0 用简单 IDLE fallback | ✅ 5 优先级 | 新建 |
| **OutputValidator** | 语言/设定校验 + 重试 prompt | ✅ 最小语言检查（CJK 比例） | ✅ 完整 | 新建 |
| **Handlers** | Farm/Fight/Forage/Mine/Talk 行为循环 | ❌ P0 不需要 | ✅ P1/P2 实现 | 新建 |
| **DailyPlanner** | 每日计划生成 + 日终评估 | ❌ P0 不需要 | ✅ | 新建 |
| **CharacterArc** | 5 阶段 + 8 里程碑 + trust_level | ❌ P0 不需要 | ✅ | 新建 |

### 3.2 @valley/core 需补的接口（P0 必需）

**MemoryBackend 接口**（让 AgentMemory 实现，方便 P2/P3 替换为 Qdrant）：

```typescript
export interface MemoryBackend {
  addConversation(role: "player" | "npc", text: string): void;
  addMemory(text: string, importance: number, entryType: string, location: string, tags: string[]): void;
  addSignificantMemory(text: string, category: string, weight: string, relatedNpcs: string[], location: string): boolean;
  getConversationContext(count?: number): string;
  getRecentMemories(count?: number): string;
  getSignificantMemoriesText(): string;
  load(): Promise<void>;
  save(): Promise<void>;
}
```

AgentMemory 实现此接口，StardewAgent 通过接口注入，未来可换 Qdrant/SQLite。

### 3.3 C# 端组件改造

| 组件 | 改动 |
|------|------|
| `DialogueBoxInputPatch.SubmitInput` | 删除 ConversationHistory/Personality/Memory 字段填充；只发 `playerInput + worldSnapshot`；消费 `response.Speech` + `response.Actions` |
| `EventHandlerInitializer.BuildDialogueRequest` | 删除（逻辑移到 TS） |
| `WebSocketClient` | 协议升级：camelCase JSON 不变，字段名调整（Text→Speech, Action→Actions[]） |
| `IAgentServerProvider.DialogueResponse` | record 重定义：`Speech` + `Actions[]` + `Emotion` |
| `PythonProcessManager` | 重命名为 `ServerProcessManager`，启动 `valley-ai-server.exe`（Bun 编译产物） |
| `CommandExecutor` | 新增 `ExecuteAction(ToolAction action)` 方法，按 `tool` 字段分发到 speak/emote/give_item 等执行器 |
| `EventHandlerInitializer` | 主事件流加 `MultiplayerHelper.ShouldRunAgentLogic` 守卫（P1 止血） |

### 3.4 边界规则

1. **C# 端零状态**：不在 C# 端保存任何对话历史、记忆、人格数据。所有状态在 TS 服务器。
2. **C# 端零解析**：不解析 `[ACTION:]` 文本标签，不解析 JSON 字符串。响应字段直接是结构化对象。
3. **TS 服务器零游戏 API**：不调用任何 StardewValley API，不访问游戏内存。所有游戏状态通过 `worldSnapshot` 显式传入。
4. **工具执行双路径**：
   - **即时工具**（speak/emote/set_state）：TS 在响应中返回，C# 立即执行
   - **延迟工具**（move_to/harvest/attack）：TS 通过 state_sync 异步推送，C# 在主线程队列执行
5. **NPC 实例隔离**：每个 NPC 独立 `Agent` 实例 + 独立 `AgentMemory` + 独立持久化文件。NPC 之间不共享状态。

---

## 4. 数据流

### 4.1 对话主流程（P0 核心）

```
玩家
 │ 1. 点击 NPC + 输入文本 + Enter
 ▼
C# DialogueBoxInputPatch.OnTextInputSubmitted
 │ 2. 收集 worldSnapshot（位置/时间/天气/附近/NPC 状态/库存/好感度/farmerName）
 │ 3. 构造 DialogueRequest{type:"dialogue", requestId, npcName, playerInput, worldSnapshot}
 │ 4. WebSocketClient.SendRequestAsync (60s 超时)
 ▼
TS ProtocolAdapter.handleDialogue(req)
 │ 5. WorldSnapshotDecoder → SceneState
 │ 6. StardewAgentRegistry.getOrCreate(npcName) → Agent 实例 + AgentMemory（已 load）
 │ 7. PromptBuilder.buildDialogueSystemPrompt(memory, scene, npcName)
 │ 8. memory.addConversation("player", playerInput)  // 立即记录玩家输入
 │ 9. Agent.prompt({messages:[{role:"user", content:playerInput}], systemPrompt})
 │ 10. agentLoop 执行：
 │     ├─ LLM 调用（带 8 个对话工具）
 │     ├─ 工具调用循环（speak/emote/remember/give_item/...）
 │     └─ shouldStopAfterTurn: 最后一轮无 toolCalls 或调用了 speak → 停止
 │ 11. 工具结果汇总：
 │     ├─ speak/show_dialogue 的 text → speech 字段
 │     ├─ 其他工具调用 → actions[] 数组
 │     └─ LLM 原始文本（无 toolCalls 时）→ 降级为 speech（兼容）
 │ 12. memory.addConversation("npc", speech)
 │ 13. memory.save() 异步落盘
 ▼
TS ProtocolAdapter → DialogueResponse{type:"dialogue_response", requestId, npcName, speech, actions[], emotion}
 │ 14. WebSocketClient 收到响应，匹配 requestId
 ▼
C# DialogueBoxInputPatch.SubmitInput
 │ 15. npc.setNewDialogue(new Dialogue(npc, null, response.Speech))
 │ 16. Game1.drawDialogue(npc)  // 原版渲染
 │ 17. foreach action in response.Actions:
 │       CommandExecutor.ExecuteAction(action)  // emote/set_state/give_item 等立即执行
 │ 18. 显示"..." 占位 → 替换为真正回复（F1/F5 修复保留）
 ▼
玩家看到 NPC 说话 + 做动作
```

### 4.2 工具执行流（混合 Action 机制）

**P0 即时工具**（在 dialogue 响应中返回）：

| 工具 | C# 执行器 | 备注 |
|------|----------|------|
| speak | 已由 SubmitInput 通过 setNewDialogue + drawDialogue 完成 | 不进 actions[] |
| show_dialogue | ActiveSpeechRouter.ShowBubble(npc, text) | 进 actions[] |
| emote | npc.doEmote(emoteId) | 进 actions[] |
| set_state | agent.StateMachine.TransitionTo(state) | 进 actions[] |
| give_item | ItemCommand.GiveToPlayer(itemId, qty) | 进 actions[] |
| give_gift | 同 give_item + 触发 gift 评估 | 进 actions[] |
| remember | TS 端吸收，不传 C# | 不进 actions[] |
| get_info | TS 端吸收，结果回 LLM | 不进 actions[] |

**P1 延迟工具**（通过 state_sync 异步推送）：

| 工具 | 触发场景 |
|------|---------|
| move_to | FOLLOW 跨图跟随、FARM/FORAGE 寻路 |
| harvest | FarmHandler 检测到成熟作物 |
| water | FarmHandler 检测到干燥土壤 |
| attack | FightHandler 检测到怪物 |
| mine | MineHandler 检测到石头 |
| forage | ForageHandler 检测到采集物 |

延迟工具流程：
```
TS agentLoop 决策循环（不在 dialogue 响应路径）
 │ → Handler 调用 tactical 工具 → ToolRegistry.execute
 │ → afterToolCall hook 把 toolCall 转成 CommandAction
 │ → 入队 state_sync 待发队列
 ▼
每 60 ticks（1秒）state_sync 推送
 │ CommandMessage{commands:[{action, parameters, reason, callId}]}
 ▼
C# OnUpdateTicked → StateSyncSender.PullPending → CommandExecutor.Execute
 │ → 执行后构造 ActionResultMessage{callId, success, result}
 ▼
TS 收到 action_result → record_tool_result(toolCallId, result)
 │ → 更新 agent._last_tool_result（修复 C3 黑洞）
 │ → 下一轮 LLM 决策能看到工具结果
```

### 4.3 持久化流

**NPC 记忆文件**：`<mod_dir>/agents/<npc_name>_memory.json`

```json
{
  "npcName": "Haley",
  "conversationHistory": [{"role":"player","text":"..."}, {"role":"npc","text":"..."}],
  "shortTermMemories": [...],
  "significantMemories": [...],
  "friendship": 250,
  "lastSavedAt": "2026-07-18T14:30:00Z"
}
```

**写入时机**：
1. 每次 dialogue 响应返回后（save 异步，不阻塞响应）
2. consolidate_day 消息触发时（日终全量压缩 + save）
3. WebSocket 断连 60s 超时后（save 同步，确保不丢数据）
4. TS 服务器收到 SIGTERM/SIGINT 时（save 同步后退出）

**加载时机**：
1. TS 服务器启动时扫描 `agents/` 目录，预加载所有 NPC memory
2. hello 消息注册 NPC 时，若 memory 未加载则按需加载

### 4.4 多 NPC 并发流

**单 WebSocket 连接 + 多 NPC 路由**：
- C# Mod 一个连接，所有 NPC 共享
- 每条消息带 `npcName` 字段路由
- TS 服务器内部 `StardewAgentRegistry` 维护 `Map<npcName, StardewAgent>`

**并发模型**：
- Bun 单进程 async，所有 Agent 共享 event loop
- LLM 调用全部 async 不阻塞 WebSocket
- LLMProvider 内部 semaphore 限并发（默认 4）
- 同一 NPC 的 dialogue 请求串行（避免对话历史错乱），不同 NPC 可并行

---

## 5. 错误处理与降级策略

### 5.1 分层降级链

```
Layer 1: LLM 完整路径（最优体验）
 │ LLM 调用成功 + 工具调用合理 + speak 产出
 ▼ 失败时
Layer 2: LLM 简化路径（功能降级）
 │ LLM 调用成功但未调 speak → 用 LLM 原始文本作 speech
 │ LLM 调用成功但工具调用异常 → 跳过 actions，仅返回 speech
 ▼ 失败时
Layer 3: RuleEngine fallback（规则兜底）
 │ LLM 重试 3 次失败 / CircuitBreaker OPEN
 │ → 本地规则生成简短回复 + 基础 emote
 ▼ 失败时
Layer 4: 静默降级（不阻断游戏）
 │ RuleEngine 也异常
 │ → 返回 "..." + question emote
 │ → 玩家可继续游戏，不卡死
```

### 5.2 P0 简化降级实现

```typescript
async function handleDialogueWithFallback(req, agent, memory, scene): Promise<DialogueResponse> {
  try {
    // Layer 1+2: LLM 路径
    const events = await runAgentLoop(agent, memory, scene, req.playerInput);
    const extracted = extractSpeechAndActions(events);
    
    if (!extracted.speech) {
      // Layer 2: LLM 未调 speak，用原始文本
      extracted.speech = extracted.rawText || "（沉默）";
    }
    
    return buildResponse(req, extracted.speech, extracted.actions, extracted.emotion);
  } catch (err) {
    // Layer 3: 简化 fallback
    console.error(`[dialogue] LLM failed for ${req.npcName}:`, err);
    return buildFallbackResponse(req, memory, err);
  }
}

function buildFallbackResponse(req, memory, err): DialogueResponse {
  let speech: string;
  let emotion: string;
  
  if (err instanceof LLMBillingError) {
    speech = "（我有点走神了，你刚说什么？）";
    emotion = "Confused";
  } else if (err instanceof LLMUnavailableError) {
    speech = "（话到嘴边说不出来...）";
    emotion = "Tired";
  } else {
    speech = "......";
    emotion = "Neutral";
  }
  
  return {
    type: "dialogue_response",
    requestId: req.requestId,
    npcName: req.npcName,
    speech,
    actions: [{tool: "emote", args: {emote_id: "question"}}],
    emotion,
    fallback: true,
  };
}
```

### 5.3 C# 端错误处理

**响应字段缺失防御**：
```csharp
var speech = response.Speech ?? "...";
var actions = response.Actions ?? Array.Empty<ToolAction>();

npc.setNewDialogue(new Dialogue(npc, null, speech));
Game1.drawDialogue(npc);

foreach (var action in actions) {
    try {
        CommandExecutor.ExecuteAction(action);
    } catch (Exception ex) {
        _monitor.Log($"Action {action.Tool} failed: {ex.Message}", LogLevel.Warn);
    }
}
```

**WebSocket 超时处理**：
```csharp
try {
    var response = await _agentServerProvider.GenerateDialogueAsync(request, ct);
    ApplyDialogueResponse(response);
} catch (TimeoutException) {
    npc.setNewDialogue(new Dialogue(npc, null, "（${npcName} 在思考...）"));
    Game1.drawDialogue(npc);
} catch (Exception ex) {
    npc.setNewDialogue(new Dialogue(npc, null, "......"));
    Game1.drawDialogue(npc);
}
```

### 5.4 CircuitBreaker 配置

```typescript
const dialogueCircuitBreaker = new CircuitBreaker({
  failureThreshold: 5,
  openDurationMs: 30_000,
  halfOpenMaxCalls: 1,
  onSuccess: () => {/* 重置 */},
  onOpen: () => { console.warn("[circuit] dialogue LLM path OPEN, using fallback"); },
  onClose: () => { console.log("[circuit] dialogue LLM path recovered"); },
});
```

### 5.5 关键不变量

| 不变量 | 检查点 |
|-------|-------|
| dialogue 响应必须包含非空 speech | TS buildResponse 时校验，空则用 "..." |
| actions 数组允许空 | 正常情况（NPC 只说话不做事） |
| 单次 dialogue 请求的 turn 数 ≤ 5 | agentLoop maxTurns 配置 |
| LLM 调用超时 ≤ 30s | LLMProvider timeout 配置 |
| WebSocket 请求超时 ≤ 60s | PendingRequestTracker 默认值 |
| NPC memory 文件大小 ≤ 1MB | save 时检查，超限触发压缩 |
| 同一 NPC 同时只有一个 dialogue 请求 | StardewAgentRegistry 内部锁 |

---

## 6. 测试策略

### 6.1 测试金字塔

```
┌─────────────────────────────────┐
│  Game E2E (TestMod)              │  P0 验收门
│  - 玩家点击→对话→响应 全链路      │
│  - 多轮对话记忆验证               │
│  - Action 执行验证               │
├─────────────────────────────────┤
│  Integration (bun test)          │  P0 必须通过
│  - WebSocket 协议兼容性           │
│  - Dialogue 端到端（mock LLM）    │
│  - Memory 持久化往返              │
│  - Fallback 降级链                │
├─────────────────────────────────┤
│  Unit (bun test)                 │  P0 必须通过
│  - PromptBuilder 模板填槽         │
│  - AgentMemory 去重/上限/持久化   │
│  - WorldSnapshotDecoder           │
│  - ProtocolAdapter 消息路由       │
│  - OutputValidator 语言检查       │
└─────────────────────────────────┘
```

### 6.2 P0 验收测试用例

**Unit Tests**（必须 100% 通过）：

| 用例 | 验证点 |
|------|-------|
| `agent-memory.test.ts` | 60s 去重窗口 / MAX_SHORT_TERM=30 上限修复 / significant memory 不重复 / load-save 往返一致 |
| `prompt-builder.test.ts` | 33 NPC 加载 / 5 阶段 prompt 注入 / 场景变量填槽 / 占位符未替换检测 |
| `world-snapshot-decoder.test.ts` | 解析 C# worldSnapshot / 字段缺失容错 / 类型校验 |
| `protocol-adapter.test.ts` | 5 消息路由 / requestId 匹配 / 错误消息处理 |
| `output-validator.test.ts` | CJK 比例检测 / 空回复拒绝 / 重试 prompt 构建 |

**Integration Tests**（必须 100% 通过）：

| 用例 | 验证点 |
|------|-------|
| `dialogue-e2e.test.ts` | mock LLM 返回 speak 工具调用 → 响应含 speech + actions / memory 写入 / 多轮对话记忆累积 |
| `dialogue-fallback.test.ts` | LLM 失败 → Layer 3 fallback / CircuitBreaker OPEN → 持续 fallback / 恢复后 CLOSE |
| `memory-persistence.test.ts` | 启动→对话→save→重启→load→记忆完整 |
| `multi-npc.test.ts` | 2 个 NPC 并发对话 / 独立 memory / 互不干扰 |
| `ws-compat.test.ts` | C# 端字段名兼容（camelCase）/ null 字段忽略 / 枚举字符串 |

**Game E2E**（TestMod，验收门）：

| 用例 | 验证点 |
|------|-------|
| `EXP001_ClickNpcOpensDialogue` | 点击 NPC → 0.1s 内开框（保留 F1 修复） |
| `EXP003_DialogueTextInputAndSend` | 输入 + Enter → TS 服务器收到 → 响应显示 |
| `DialogueMemory` | 第 1 轮说"我叫张三" → 第 2 轮问"我叫什么" → NPC 回答"张三" |
| `ActionExecution` | 玩家说"送我东西" → NPC 调 give_item → C# 执行 + 物品入背包 |
| `EmoteExecution` | NPC 调 emote → C# 显示表情泡 |
| `FallbackPath` | 断开 TS 服务器 → 60s 内 dialogue 仍可响应（fallback） |
| `Reconnect` | 断网→重连→对话恢复正常 |

### 6.3 黄金集对比（Protocol 兼容性）

| 消息类型 | 黄金集字段 | TS 必须匹配 |
|---------|----------|------------|
| `hello` | `{type, requestId, status:"ok"}` | 完全匹配 |
| `ping` | `{type:"pong", requestId}` | 完全匹配 |
| `dialogue` | `{type, requestId, npcName, text, emotion, action}` | TS 字段名不同（speech/actions[]），需 C# 端配合升级 |
| `tool_call_result` | `{type:"ack"}` | 完全匹配 |
| `action_result` | `{type:"ack"}` | 完全匹配 |

### 6.4 静态检查

| 工具 | 配置 | 阻断条件 |
|------|------|---------|
| `tsc --noEmit` | strict: true, noUnusedLocals: true | 任何 error/warning |
| `biome check` | 推荐配置 | 任何 error/warning |
| `dependency-cruiser` | core 不依赖 stardew | 违反依赖方向 |
| `dotnet build` | TreatWarningsAsErrors: true | 任何 warning |
| `SMAPI build` | 配置文件检查 | manifest 缺字段 |

### 6.5 性能基线

| 指标 | 目标 | P0 验收 |
|------|------|--------|
| 点击 NPC → 对话框打开 | ≤ 100ms | 保留 F1 修复 |
| 玩家 Enter → dialogue 响应到达 | ≤ 5s（含 LLM） | MiniMax-M2 实测 2-4s |
| TS 服务器冷启动 | ≤ 2s | Bun exe 启动 |
| TS 服务器内存（10 NPC） | ≤ 200MB | bun:sqlite + JSON |
| C# OnUpdateTicked 增量 | ≤ 0.1ms | 仅 state_sync 检查 |
| Memory save 单次 | ≤ 50ms | JSON 文件写入 |

### 6.6 测试执行命令

```bash
# TS 单元 + 集成测试
cd D:\Source\ValleyAI
bun test

# TS 静态检查
bun run typecheck
bun run check:imports

# C# 静态检查 + 构建
cd D:\Source\ValleyTalk\src\ValleyAgent
dotnet build -c Release

# 游戏内 E2E
cd D:\Source\ValleyTalk
.\scripts\test\run-game-tests.bat
```

---

## 7. 分阶段实施计划

### 7.1 P0：对话管线止血（最小可交付）

**目标**：NPC 能记得住、说到做到、不乱码。

**范围**：
- TS 服务器实现 dialogue + hello + ping + tool_call_result + action_result 5 个消息
- C# Mod 切换到 TS 服务器，废弃 Python
- 实装 B1（对话记忆三处打通）+ C1（[ACTION:] 剥离，改为 TS 端工具调用）+ C2（Action 接入 C#）
- Bun exe 打包 + ServerProcessManager

**验收门**：
- 全部 Unit + Integration + Game E2E 测试通过
- 玩家实测：多轮对话 NPC 记得玩家说过的话
- 玩家实测：说"送我东西" → NPC 调 give_item → 物品入背包

### 7.2 P3：IAgentEntity 抽象 + 隐藏 Farmer 验证

**目标**：执行末端实体抽象层重构，验证隐藏 Farmer 方案。

**范围**：
- 引入 `IAgentEntity` 接口（位置/朝向/说话/emote/动画 + 移动子系统）
- 把 43 处 NPC 耦合点收敛到接口后面
- 实现 `NpcEntity`（NPC 实现）+ `FarmerEntity`（Farmer 外壳实现）+ `EntityMapper`（双实体映射）
- 接线 `AgentSyncBroadcaster` + `AgentRemoteRenderer`（修复 E1 联机孤儿）
- 主事件流加 multiplayer 守卫（修复 E2）

**验收门**：
- IAgentEntity 接口稳定，NpcEntity 行为与原 NPC 完全一致
- FarmerEntity 单独 spike 验证：跨图同步 + 远端地图激活 + 联机移动无橡皮筋
- 单人游戏无回归（所有 P0 测试仍通过）

### 7.3 P1+P2：在干净架构上重写

**目标**：完成场景切换、记忆闭环、工具结果回注。

**范围**：
- D1/D2/D3 场景切换修复（在 IAgentEntity 接口上重写）
- B2 npc_prompts.json 全量接入（33 NPC × 5 阶段）
- B3 remember/forget 工具注册（已在 P0 实现 remember，补 forget）
- C3 工具结果回注 LLM（修复 `_last_tool_result` 黑洞）
- B4 短期记忆去重 + MAX_SHORT_TERM 上限修复（已在 e2e AgentMemory 修复，正式化）
- B5 entryType 字段对齐（TS 端统一 camelCase）
- E3/E4 联机移动控制权 + 多玩家对话

**验收门**：
- 全部 P0 + P1 + P2 测试通过
- 联机 2 人实测：NPC 状态同步无冲突
- 33 NPC 全量人格数据生效

---

## 8. 风险与缓解

| 风险 | 缓解 |
|------|------|
| Bun exe 在某些 Windows 环境无法运行 | 保留 `bun run` fallback 模式（devMode 配置） |
| LLM 响应延迟高于 5s | CircuitBreaker + Layer 3 fallback，玩家不卡死 |
| TS 服务器内存泄漏（长会话） | 每次 dialogue 后 memory.save + 内存监控告警 |
| C# Mod 升级后存档不兼容 | memory 文件首次加载时迁移旧格式（Python 版字段名兼容） |
| 隐藏 Farmer 方案在 1.6 验证失败 | P3 分两步：先抽象接口，FarmerEntity 作为独立 spike 可回退 |
| 工具调用死循环（LLM 反复调同一工具） | agentLoop maxTurns=5 + beforeToolCall 钩子检测重复调用 |

---

## 9. 待用户决策的开放问题

以下问题影响 P0 实现，需用户在 spec 审核阶段回答：

1. **Bun exe 体积**：~40-50MB 是否可接受？是否需要压缩？（不阻塞 P0，可后置）
2. **TS 服务器配置传递**：P0 默认用命令行参数（`--port` / `--llm-api-key` / `--llm-model`），配置文件 `server-config.json` 作为 fallback。命令行优先级最高。如不同意请指示。
3. **multiplayer 守卫范围**：P3 阶段在 `OnUpdateTicked` / `OnPlayerWarped` / `OnSaveLoaded` 三个关键事件加 `MultiplayerHelper.ShouldRunAgentLogic` 守卫，其他事件（`OnDayStarted`/`OnDayEnding` 等纯数据同步事件）不加。如需扩大范围请指示。
4. **FarmerEntity spike 验证标准**：P3 验收门要求 `EXP004_CrossMapFollow` + `EXP005_NpcFarmActionVisible` + `Func_MultiplayerSync` 三个 TestMod 用例通过。
5. **memory 文件迁移**：P0 不迁移 Python 版历史数据，NPC memory 清零重开。旧 `Haley_memory.json` 保留备份但 TS 服务器不读。如需迁移请指示。
