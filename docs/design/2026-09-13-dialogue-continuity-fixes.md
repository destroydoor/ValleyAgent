# 对话连续性修复设计（Issue #8，PR1）

> **日期**：2026-09-13
> **状态**：待用户审阅（审阅通过后派 subagent 实施）
> **关联**：Issue #8（对话连续性受限）、Issue #9（Agent 概念演进，见 `2026-09-13-agent-body-refactor.md`，分两个 PR）
> **纪律**：本文事实台账全部经代码核实（§2），修复只基于已验证事实；与 Issue #8 原文描述有出入处以本文为准

---

## 1. 问题定性

Issue #8 报告的现象：连续 AI 对话被拒、新装用户疑似 30 秒冷却、房客"无法 AI 对话"、BUSY 灰字无排队。

核实结论：**现象真实，但机制定位需要修正**——

- 冷却 30s 兜底存在，但**冷却在生产路径上是死代码**（§2 F2），玩家"连续对话被拒"不是冷却造成的；
- 真正造成"被拒感"的是 **TS 侧 per-NPC 会话锁 + fallback 文案混淆**（§2 F4/F5）；
- 房客"无法 AI 对话"的主体是**房客只对 Agent 村民生效**的过滤——这部分属于 Issue #9 的架构重构（PR2），本 PR 不动，只修 PR1 范围内的连续性与文案问题。

## 2. 事实台账（已验证）

### F1 冷却默认值有三处，互相不一致

| 位置 | 值 | 说明 |
|---|---|---|
| `src/ValleyAgent/Config/ModConfig.cs:530` | `DialogueCooldownSeconds = 3` | 配置默认，GMCM 暴露（GMCMIntegration.cs:476-481，钳 [1,60]） |
| `src/ValleyAgent.Abstractions/Api/DialogueStateManager.cs:28` | `DialogueCooldownMs = 30000` | 共享态默认，仅当初始化未流入时生效 |
| `src/ValleyAgent/Api/ValleyAgentApi.cs:116,206` | `?? 30000` | API 兜底，SharedStateOrNull 为 null 时生效 |

初始化链 `ServiceInitializer.cs:169` 用 `config.DialogueCooldownSeconds * 1000` 覆盖，故**正常启动的新装用户拿到 3 秒，不是 30 秒**；30s 只在共享态为 null（初始化前窗口）命中。

### F2 冷却在生产路径是死代码

`DialogueStateManager.TryStartDialogueRequest`（唯一冷却判定，DialogueStateManager.cs:61-101）的唯一上游是 `DialogueManagementApi.TryGenerateDialogue`（DialogueManagementApi.cs:50）；而 `ValleyAgentApi.TryGenerateDialogue` 在生产代码中**无调用方**（仅 TestMod 与 EXP 测试使用）。

真实玩家路径全部绕过冷却直连 provider：

| 路径 | 证据 |
|---|---|
| 主机对话框输入 | `DialogueBoxInputPatch.SubmitInput` → `GenerateDialogueAsync`（DialogueBoxInputPatch.cs:331） |
| 聊天栏（主机/房客） | `ChatBarRouter.SendDialogueAsync` → provider/transport（ChatBarRouter.cs:477-479） |
| 房客中继（主机侧处理） | `HostRequestHandlers.HandleDialogueRequest` → provider（HostRequestHandlers.cs:126） |

推论：**GMCM 的冷却设置对真实玩家对话无效**；玩家聊天实际由 TS 会话锁天然串行（F4）。

### F3 BUSY 的真实机制：TS per-NPC 锁，持锁覆盖整个 LLM 运行

- `protocol-adapter.ts:593-599`：`handleDialogue` 开头 `registry.acquireLock(npcName)`，失败 → `buildBusyResponse`；`finally releaseLock`（:752-754）。
- `stardew-agent-registry.ts:123-131`：锁是简单的 `Set`，**无等待、无排队**。
- 持锁期 = 整个 ReAct 循环 + 工具同步执行 + execute_adjust 回执等待，实测可达数十秒（LLMTimeoutSeconds=120）。
- BUSY 响应：`protocol-adapter.ts:757-769`，`fallback=true`，speech="（xxx 正在和别人交流）"。

### F4 fallback=true 有两种来源，C# 不区分、统一渲染"正在和别人交流"

| 来源 | 证据 | speech |
|---|---|---|
| 会话锁 BUSY | `protocol-adapter.ts:757-769` | "（xxx 正在和别人交流）" |
| LLM 失败兜底 | `rule-engine.ts:9-37`（buildFallbackResponse 也返回 `fallback: true`） | "（我有点走神了…）/（话到嘴边说不出来...）/......" |

C# 侧 `DialogueBoxInputPatch.ProcessPendingReplies`（DialogueBoxInputPatch.cs:382-393）对任何 `reply.Fallback` 硬编码渲染灰字"他/她/它正在和别人交流"——**LLM 故障被误报成"正在和别人交流"**，误导排查与体验。

### F5 同一玩家不会自我撞锁

- 对话框：`_isWaitingForResponse` 阻塞 Enter（DialogueBoxInputPatch.cs:219-222）；
- 聊天栏：`TryReserveInflight` per-NPC 去重（ChatBarRouter.cs:451-465）。

BUSY 实际命中场景：**多玩家同时聊同一 NPC**、**关框/重开时上一轮仍在途**（迟发请求撞上未释放的锁）。

### F6 房客占线回执现状

- 房客**对话框**路径：fallback 标志随广播回包（AgentSyncBroadcaster.cs:124-146），房客 `DialogueBoxInputPatch.ProcessPendingReplies` 同样渲染灰字（2026-08-23 审计已接通）。
- 房客**聊天栏**路径：`ChatBarRouter` 不处理 fallback 标志（无引用），BUSY/失败回包按普通台词渲染 TS 的 speech 文本（可见但不走灰字样式）。**待实施时先验证渲染样式，再决定是否对齐**（P2）。

## 3. 修复方案（PR1 范围）

### R1（TS）会话锁加限时等待，替代"立即拒绝"

- `StardewAgentRegistry.acquireLock` 改为带超时等待：默认 **15s**，250ms 轮询，可经配置覆盖；`handleDialogue` 传参。
- 等待期内第二请求排队，锁释放后正常进入对话——覆盖"上一轮还在想、玩家接着说"的主场景；
- 超时才返回 `buildBusyResponse`（此时带 fallbackReason，见 R2）。
- 15s < C# 侧 LLMTimeoutSeconds=120，不引入新的超时冲突。
- 不做 C# 端排队自动补发（复杂度高：顺序、超时、房客 requestId 配对；TS 端等待已覆盖主场景，复杂度收益比不合算）。

### R2（协议）dialogue_response 增加 `fallbackReason` 可选字段

- 取值约定：`busy`（会话锁超时）/ `llm_error`（生成失败）/ `billing` / `unavailable`；缺省 = 旧客户端兼容。
- `server/protocol/messages.json` dialogue_response.fields 增加该字段（required=false）；
- TS：`buildBusyResponse` 带 `fallbackReason: "busy"`；`rule-engine.buildFallbackResponse` 按异常类型带 `llm_error`/`billing`/`unavailable`；
- C#：`DialogueResponse` record（IAgentServerProvider.cs:98-114）加 `string? FallbackReason = null`；
- `AgentSyncMessages.DialogueResponseMessage`（房客广播契约）同步加字段，端到端透传。
- check:protocol 跟随更新。

### R3（C#）fallback 灰字文案以 TS speech 为单一来源

- `DialogueBoxInputPatch.ProcessPendingReplies` 的 fallback 分支改为：灰字渲染 **TS 回传的 speech 文本**（busy="（xxx 正在和别人交流）"、失败="（我有点走神了…）"），删除 C# 硬编码的代词文案；
- `fallbackReason` 用于日志留痕（诊断不再被"正在和别人交流"误导）与房客广播透传；
- 文案单一来源在 TS，C# 不再重复维护一套话术（2026-08-16 决策 #3 的"灰字系统提示"样式不变）。

### R4（C#）冷却默认值一致性修正

- `DialogueStateManager.DialogueCooldownMs` 默认 30000 → **3000**；`ValleyAgentApi` 两处 `?? 30000` → `?? 3000`（消除初始化前窗口的 30s 陷阱，与 ModConfig 默认对齐）；
- GMCM 冷却 tooltip 补一句："仅作用于外部 API/测试通道；玩家聊天由服务器会话锁自动串行"——把 F2 的死代码语义对用户讲真话；
- **不把冷却接入玩家路径**（设计哲学 7：TS 锁已串行且 LLM 时长 ≫ 冷却值，接入无收益）。"玩家主动对话 vs AI 主动发言两套节奏"事实上已存在：玩家聊天=会话锁，AI 主动发言=ProactiveQuota 每日额度。

### R5（P2，视实施验证结果）房客聊天栏 fallback 灰字对齐

- 若 §2 F6 验证确认聊天栏把 BUSY/失败渲染成普通台词，则 ChatBarRouter 回包处理补 fallback → 灰字；否则跳过。

## 4. 明确不做（本 PR）

- 房客对非 Agent 村民开放 AI 对话 —— 属 PR2（Issue #9）；
- 冷却接入玩家路径 / 删除冷却概念 —— 维持 API 通道现状 + 值对齐；
- C# 端 BUSY 排队自动补发 —— R1 的 TS 等待已覆盖主场景。

## 5. 验证方案

### 自动化门槛（全绿才可交付）

1. `bun test packages/stardew`（新增：锁等待成功/超时 BUSY/fallbackReason 字段单测）
2. `tsc --noEmit`、`check:protocol`（messages.json 与 TS/C# DTO 同步）
3. C# 编译 0 警告；xUnit（新增：FallbackReason 反序列化与灰字文案选择逻辑）

### 故障注入判据

| 场景 | 判据 |
|---|---|
| 同一 NPC 并发两个 dialogue | 第二个在 15s 内等到锁并正常回复（非 BUSY）；持锁 >15s 时第二个得 busy |
| mock LLM 抛错 | 响应 fallbackReason=llm_error，C# 灰字显示"（我有点走神了…）"而非"正在和别人交流" |
| 旧 TS 客户端（无 reason 字段） | C# 反序列化不炸，回退现行为 |

### 实机判据（用户验收，游戏内测试由用户执行）

1. 连续两轮对话（上一轮刚回完接着说）无"正在和别人交流"误报；
2. 关框立刻重开再发消息：要么稍等后正常回复（锁等待生效），要么灰字提示正确；
3. 断开 LLM 网络：灰字是"走神"类文案，不是"正在和别人交流"；
4. 主机+房客同时聊同一 NPC：后到者得到正确灰字提示（或等待后接上），房客对话框可见。

## 6. 实施拆分（subagent 任务）

| # | 任务 | 文件 |
|---|---|---|
| S1 | TS 锁限时等待 + 单测 | `server/packages/stardew/src/stardew-agent-registry.ts`、`protocol-adapter.ts` |
| S2 | fallbackReason 协议字段（TS+messages.json+C# DTO+广播契约） | `protocol-adapter.ts`、`rule-engine.ts`、`server/protocol/messages.json`、`IAgentServerProvider.cs`、`AgentSyncMessages.cs` |
| S3 | C# 灰字文案改造 + 冷却默认值修正 + GMCM tooltip | `DialogueBoxInputPatch.cs`、`DialogueStateManager.cs`、`ValleyAgentApi.cs`、`GMCMIntegration.cs` |
| S4 | C# 单测 + 房客聊天栏 fallback 验证（R5 判定） | `src/ValleyAgent.UnitTests/` |

> S2 是 S1/S3 的前置（字段先落协议），S1 与 S3 可并行。
