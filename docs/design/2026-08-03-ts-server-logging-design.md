# TS 服务器日志可观测性改进设计

> 日期：2026-08-03
> 状态：设计稿（不含代码改动）
> 依据：对 `D:/Source/ValleyAI`（TS 智能层）与 `D:/Source/ValleyTalk`（C# 执行层）现状代码的实证核实。文中 `文件:行号` 均为现状。
> 跨仓库说明：本设计改动落在 `D:/Source/ValleyAI`（TS），C# 侧仅作为日志通道现状核实，不改 C#。

---

## 0. 现状核实：为什么日志"没有有价值信息"

### 0.1 核心智能层零 stdout 日志

`packages/core/src/` 全目录**零** `console.*` 调用（grep 计数）。最关键的三处排障盲区：

| 文件 | 静默的关键信息 |
|---|---|
| `llm-provider.ts` | LLM 调用全程无日志：provider/model/token 数/耗时/重试次数/错误分类（billing vs unavailable）。`withRetry`（156-190）的退避、`acquireSlot`（372-383）的并发排队、HTTP 错误分类（170-178）全部静默 |
| `agent-loop.ts` | 决策循环零日志：每轮 turn、工具调用、停止原因。仅通过 `EventStream` emit 事件（57/66/87-89/166/202），但事件本身不是日志 |
| `circuit-breaker.ts` / `token-budget.ts` / `performance-monitor.ts` | 熔断触发/恢复、token 预算消耗、性能指标全部静默 |

### 0.2 agentLoop 事件流有信息，但 stdout 无消费者

`agent-loop.ts` 已 emit 完整决策事件：`turn_start` / `message_end`（含 LLM 原始输出 content）/ `tool_call_start`（含 args）/ `tool_call_end`（含 result）/ `error` / `agent_end`。

当前唯一消费者是 [transcript-recorder.ts:100](file:///D:/Source/ValleyAI/packages/stardew/src/transcript-recorder.ts#L100) 的 `attach(agent)`，它把事件**写进留痕文件**（TranscriptStore），**没有任何消费者把事件打到 stdout**。所以排障时 stdout 看不到 NPC 想了什么、输出了什么、调了什么工具——这些恰好是最该看的。

[stardew-agent.ts:436](file:///D:/Source/ValleyAI/packages/stardew/src/stardew-agent.ts#L436) `recorder?.attach(this.coreAgent)` 是 attach 的唯一位置，新增 sink 在此并列即可。

### 0.3 协议层日志单薄且 dialogue 主路径是盲区

`protocol-adapter.ts` 现有 13 处 console，集中在 `state`（72）/ `consolidate`（82）/ `route_shout`（94）/ `tool_result`（114）四类。核心的 `handleDialogue`（156）**无任何收发日志**——玩家说了什么、NPC 回了什么、响应带几个 action、好感度变化，stdout 全看不到。

### 0.4 导演 Director 零日志且未接线

[director.ts](file:///D:/Source/ValleyAI/packages/stardew/src/director.ts) 的 `morningPlan`（131）/ `milestoneReact`（149）/ `callLlmForBeats`（205）全程静默：调 LLM、解析 JSON、验证丢弃 beat、`recordPlanRun` 终态全无日志。且 `new Director` 在 `packages/stardew/src` 下**零匹配**——Director 当前未在 server 实例化（[director.ts:106](file:///D:/Source/ValleyAI/packages/stardew/src/director.ts#L106) 注释明示），其留痕接缝 `transcriptStore` 缺省 `undefined` → no-op。

### 0.5 63 处裸 console 无统一格式

全仓库 63 处 `console.*` 散落 10 文件，前缀混用 `[server]` / `[valley-ai-server]` / `[AgentMemory]` / `[transcript]` / `[state]` / `[tool]` 等，无级别、无 NPC 上下文、无统一 timestamp。

### 0.6 stdout 通道现状（C# 侧，不改）

TS server 由 C# `ServerProcessManager` 拉起：
- `ConsoleWindow=true`（默认，[ServerProcessManager.cs:36](file:///d:/Source/ValleyTalk/src/ValleyAgent/WebSocket/ServerProcessManager.cs#L36)）：TS server 在独立 cmd 窗口运行，stdout 直接显示，不重定向。
- `ConsoleWindow=false`：stdout 被捕获，每行前缀 `[Server]`/`[Server:ERR]` 经 `LogCallback`（[ServerProcessManager.cs:241-248](file:///d:/Source/ValleyTalk/src/ValleyAgent/WebSocket/ServerProcessManager.cs#L241)）转发到 SMAPI 日志。

两种模式下"完整原始"日志均可行：cmd 窗口可滚屏，SMAPI 日志文件可 grep。**TS 侧无需自带文件输出**，避免与 SMAPI 日志重复。

---

## 1. 设计目标与范围

### 目标
排障时能从 stdout 立刻看到：agentLoop 每轮决策（思考过程+输出）、工具调用（参数+结果+耗时）、导演故事设计、协议层关键收发。

### 范围内
- 新增事件流 stdout sink（packages/stardew）
- llm-provider 埋点（packages/core）
- protocol-adapter 收发埋点（packages/stardew）
- director 埋点（packages/stardew，暂不生效）

### 范围外
- 不引入统一 logger 模块（用户选定的方案 A：裸 console）
- 不改 transcript-recorder / 不改 C# / 不改 63 处已有裸 console
- 不接线 Director（超出"日志改进"范围，仅埋点待后续接线生效）
- 不做熔断/token-budget/performance-monitor 埋点（本轮聚焦用户指定优先级，这些可后续补）

---

## 2. 方案选型

| 方案 | 说明 | 优点 | 缺点 | 取舍 |
|---|---|---|---|---|
| A 裸 console（选定） | 事件流 sink + 定点 console 埋点，不引入 logger | 改动最小、不动 core 接口、复用现有事件 | 完整原始无法按级别开关，平时也刷屏 | **用户选定**，接受刷屏换取零基建成本 |
| B 统一 logger | 极简 logger + 级别过滤 | 完整原始可开关 | 多一个模块要写要测 | 未选 |
| C tail 留痕 | 读 transcript-store | 零侵入 | 非实时、信息不全 | 未选 |

选定 A 的后果：完整原始日志体积大，平时会刷屏。缓解措施——统一前缀格式（§3.5）便于 grep 过滤；用户已确认接受。

---

## 3. 详细设计

### 3.1 新增 `packages/stardew/src/console-log-subscriber.ts`

仿 `RunTranscriptRecorder` 的 `attach(agent)` 模式，订阅事件实时打 stdout。完整原始，不截断。

订阅的事件与输出：

| 事件 | 输出示例 |
|---|---|
| `turn_start` | `[15:30:01.234] [turn] Abigail #0 start` |
| `message_end` | `[15:30:03.456] [turn] Abigail #0 llm输出:\n<完整原始 content>` |
| `tool_call_start` | `[15:30:03.500] [tool] Abigail #0 → speak args=<完整 JSON>` |
| `tool_call_end` | `[15:30:03.620] [tool] Abigail #0 ← speak ok=true 120ms result=<完整>` |
| `turn_end` | `[15:30:03.621] [turn] Abigail #0 end 2387ms` |
| `error` | `[15:30:03.622] [turn] Abigail #0 error: <message>` |

实现要点：
- 构造时传入 `npcName`，每条日志带 NPC 名（per-NPC 对话是核心场景，无 NPC 关联则日志无法追踪）。
- 耗时用事件自带 `timestamp` 差计算（`tool_call_end.timestamp - tool_call_start.timestamp`），不引入额外计时器。
- `attach(agent: Agent)` 调 `agent.subscribe`，返回 unsubscribe 函数；run 结束 dispose。
- 在 [stardew-agent.ts runOnce](file:///D:/Source/ValleyAI/packages/stardew/src/stardew-agent.ts#L436) 里和 `recorder?.attach` 并列调用。runDialogue / runBeat 共用（都走 runOnce）。
- 全部输出 best-effort，try/catch 包裹，绝不向上抛（仿 transcript-recorder 防御）。

### 3.2 `packages/core/src/llm-provider.ts` 埋点

事件流只携带 LLM 输出文本，不携带元信息。以下信息只能在 provider 内部埋点：

| 位置 | 输出示例 |
|---|---|
| `doCall`/`doCallWithTools` 调用前 | `[15:30:01.100] [llm] → provider=minimax model=abab6.5s-chat msgCount=5 tools=9` |
| 调用后 | `[15:30:03.456] [llm] ← ok in=320 out=180 2356ms` |
| `withRetry` 重试 | `[15:30:03.460] [llm] retry 1/3 (HTTP 503: timeout) backoff=1000ms` |
| `withRetry` billing | `[15:30:03.461] [llm] billing/rate-limited HTTP 402 → 不重试` |
| `withRetry` 最终失败 | `[15:30:06.000] [llm] unavailable after 3 retries: <msg>` |
| `acquireSlot` 排队>0 | `[15:30:01.101] [llm] queue wait=850ms` |

实现要点：
- `msgCount`/`tools` 从入参取；`in`/`out` 从 `result.usage` 取；耗时用 `Date.now()` 差（perf.measure 已有，但日志要即时数字）。
- `tools` 数量：`chatWithTools` 走 `doCallWithTools` 有 tools 参数；`chatCompletion` 走 `doCall` 无 tools，日志省略 tools 字段。
- `callOverride` 路径（测试用）也埋点，标注 `override=true`，便于区分真实 LLM 调用与测试桩。
- billing/重试/最终失败的日志在 `withRetry`（156-190）内补。

### 3.3 `packages/stardew/src/protocol-adapter.ts` 收发埋点

dialogue 主路径当前是盲区，重点补。已有 state/consolidate/route_shout/tool_result 日志保留。

| 位置 | 输出示例 |
|---|---|
| `handleHello` 收 | `[15:30:00.000] [recv] hello agent=Abigail` |
| `handleDialogue` 收 | `[15:30:00.100] [recv] dialogue npc=Abigail player="<完整输入>"` |
| `handleDialogue` 发 | `[15:30:03.700] [send] dialogue npc=Abigail speech="<完整台词>" actions=2 emotion=happy friendship=+5` |
| `handleDialogue` 兜底 | `[15:30:04.000] [send] dialogue npc=Abigail FALLBACK reason=<LLMBillingError>` |
| `handleActionResult` 收 | `[15:30:05.000] [recv] action_result npc=Abigail tool=give_gift ok=false reason=inventoryFull result="<完整>"` |
| `handleStateChanged` 收 | `[15:30:06.000] [recv] state_changed npc=Abigail IDLE→TRAVEL forced reason=travel_failed` |

实现要点：
- 收发日志成对：`handleDialogue` 入口打 `recv`，返回前打 `send`（含兜底路径）。
- 玩家输入完整不截断；NPC 台词完整不截断。
- `actions` 数量、`emotion`、`friendshipDelta` 从 `DialogueResult` 取。

### 3.4 `packages/stardew/src/director.ts` 埋点（暂不生效）

⚠️ Director 未在 server 实例化，以下埋点完成后需等 Director 接线才会输出。本轮仍埋点，避免接线时再补。

| 位置 | 输出示例 |
|---|---|
| `morningPlan` 开始 | `[06:00:00.000] [director] morningPlan start` |
| `callLlmForBeats` 后 | `[06:00:02.000] [director] llm输出:\n<完整>` |
| `validateAndFilter` 后 | `[06:00:02.100] [director] beats: 产出2 丢弃1(冷却)` |
| `recordPlanRun` 终态 | `[06:00:02.200] [director] morningPlan end status=completed produced=2` |

### 3.5 格式约定

即使裸 console，新埋点统一前缀，便于 grep：

```
[HH:MM:SS.mmm] [tag] <npc?> <内容>
```

- `timestamp()`：复用 protocol-adapter 现有的 `timestamp()` 函数（已生成 `HH:MM:SS.mmm` 格式）。若该函数未导出，在 console-log-subscriber 内本地实现相同格式，避免跨模块依赖。
- `tag`：`recv` / `send` / `llm` / `turn` / `tool` / `director` / `server`。
- `<npc?>`：对话/决策类日志带 NPC 名；协议层 hello/ping 等无 NPC 的省略。
- 完整原始：内容不截断。多行内容（如 LLM 输出）直接换行跟在标签行后。
- 已有 63 处裸 console 不强制改（避免扩大范围），仅新埋点遵循此格式。

---

## 4. 错误处理

- 所有日志 best-effort：try/catch 包裹，绝不向上抛、不阻塞主流程（仿 [transcript-recorder.ts:116-118](file:///D:/Source/ValleyAI/packages/stardew/src/transcript-recorder.ts#L116) 防御）。
- 格式化大对象（工具 args/result）用 safe-stringify（`JSON.stringify` try/catch + 循环引用 fallback `String()`），防循环引用导致 console 抛错。
- `console.log` 本身不抛，但防御性包裹仍保留，统一风格。
- sink 订阅绝不影响 agentLoop 事件流：subscribe 失败不阻断 Agent.prompt。

---

## 5. 测试验证

### 5.1 单元测试
- **console-log-subscriber**：构造 mock Agent 事件序列（turn_start → message_end → tool_call_start → tool_call_end → turn_end），spy `console.log`，断言输出格式、NPC 名、完整内容、耗时计算正确。
- **llm-provider 埋点**：现有 [llm-provider.test.ts](file:///D:/Source/ValleyAI/packages/core/tests/llm-provider.test.ts) 用 `callOverride`，spy `console.log` 断言 `→`/`←`/retry/billing 行触发；callOverride 路径标注 `override=true`。
- **protocol-adapter**：现有 [protocol-adapter.test.ts](file:///D:/Source/ValleyAI/packages/stardew/tests/protocol-adapter.test.ts) spy `console.log` 断言 `recv`/`send` 成对、内容完整。
- **director**：现有 [director.test.ts](file:///D:/Source/ValleyAI/packages/stardew/tests/director.test.ts) spy `console.log` 断言 morningPlan 产出/丢弃日志。

### 5.2 集成测试
[dialogue-e2e.test.ts](file:///D:/Source/ValleyAI/packages/stardew/tests/dialogue-e2e.test.ts) 跑一次完整对话，重定向 stdout 捕获，断言关键日志行存在：`[turn] #0 start` / `[llm] →` / `[llm] ←` / `[tool] → speak` / `[recv] dialogue` / `[send] dialogue`。

### 5.3 验收门槛（铁律3）
1. `bun test packages/core` 全绿
2. `bun test packages/stardew` 全绿
3. `tsc --noEmit` 全绿（packages/core + packages/stardew）
4. `bun run check:protocol` 不回归
5. 0 警告 0 错误（铁律1、2）
6. 手动跑一次对话，确认 cmd 窗口 stdout 出现完整原始的 turn/llm/tool/recv/send 日志（多模态验证：截图/录屏）

---

## 6. 不做清单

- 不引入 logger 模块（方案 A）
- 不改 transcript-recorder（留痕不变）
- 不改 C# 侧（stdout 通道已通）
- 不改 63 处已有裸 console（避免扩范围）
- 不接线 Director（超范围）
- 不做熔断/token-budget/performance-monitor 埋点（本轮聚焦用户指定优先级）

---

## 7. 后续可扩展

- 若完整原始刷屏成为痛点，可渐进升级为方案 B：在现有埋点上包一层极简 logger，加级别过滤。本设计的统一前缀格式（§3.5）为该升级预留了切分点。
- Director 接线后，§3.4 埋点自动生效。
- 熔断/token-budget 埋点可按本设计同格式补，无需改架构。
