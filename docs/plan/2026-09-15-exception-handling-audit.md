# 2026-09-15 异常处理机制审计 —— "假设每个功能都有 bug" 压力视角

> **审计问题（用户原话）**：审查项目的异常处理机制，默认假设每一个功能都是有错误的，这种情况下模组还能否正常运行，并在 log 或者 CLI 中突出正确的、有价值的错误信息？
>
> **一句话结论**：**两个都不成立。** ①"任一功能出错而整体仍能跑"目前只在 TS 服务器侧基本成立，C# 模组侧存在多个**单点故障面**（一个子系统抛出即让整条 tick 流水线停摆 / 一次意外异常即让重连与心跳永久死亡 / 一次初始化异常即让整个模组半死且无降级）；②"错误信息有价值"存在系统性缺陷，其中**一条 P0 会把错误详情从唯一的持久化日志里彻底销毁**（实测 `console.error(..., err)` 落盘为 `{}`），另有 139 处 catch 只记 `ex.Message`（丢类型丢栈）、47 处错误线索落在 SMAPI 默认不可见的 Trace 级。
>
> **可复现产物**（本批提交）：
> - `scripts/check-exception-hygiene.mjs` —— 12 条规则的静态扫描器 + 基线棘轮门禁（当前存量 **297 处：P0 39 / P1 211 / P2 47**）
> - `scripts/exception-hygiene-baseline.json` —— 存量基线（CI 只拦新增，修完一批跑 `--update-baseline` 收紧）
> - `scripts/test/error-observability-probe.ts` —— 打真实 TS 服务器的可观测性探针（本文档 §8 的全部实测输出来自它）
> - `scripts/test/agent-wedge-repro.ts` —— Agent 事件流卡死复现（§3.8，已实测复现：`awaitAll-HUNG` + `isIdle(): false`）
>
> **遗留项全部转 GitHub issue（#21–#28）**：本批只交付审计底稿 + 门禁 + 探针，**未改任何生产代码**（C# 侧本机无 dotnet，不满足 `AGENTS.md §5` 的验证门槛）。

---

## 0. 结论速览

| 问题域 | 判定 | 一句话依据 |
|---|---|---|
| **TS Agent Server 出错仍能跑？** | 🟡 **基本成立** | WS 消息有顶层 try/catch、LLM 失败有人设三档兜底、工具异常转 `isError` 回喂 LLM、账本原子写、留痕/日志器全 best-effort —— 但 `Agent.prompt` 的 `.then()` 无 `.catch()`（NPC 可被永久锁 BUSY）、`NpcPromptLoader` 构造无守卫（人设文件坏 = 进程秒退） |
| **C# 模组出错仍能跑？** | 🔴 **不成立** | `OnUpdateTicked` 397 行零 try/catch 串起 10+ 子系统；8 处 Harmony 补丁入口无外层守卫（异常直接注入游戏 draw/input/checkAction 调用栈）；WS 三个长跑循环只捕 `WebSocketException`/`OperationCanceledException`；初始化失败 `throw` 且无降级模式 |
| **log / CLI 能突出有价值的错误？** | 🔴 **不成立** | 实测：落盘日志里 `[error] [server] message handler error: {}`、`[error] [dialogue] LLM failed for Haley: {}` —— 错误 message 与栈被 `JSON.stringify(Error)==="{}"` 销毁；139 处只记 `ex.Message`；47 处降级/丢弃信号落 Trace；16 处失败事件打在 `console.log`；TS 回的 `error` 帧不带 requestId 且字段名与 C# 读取端不一致（C# 只会说 "Unknown error"） |
| **玩家侧降级体验** | 🟢 **有设计** | BUSY/LLM 故障走灰字 + `fallbackReason` 三档、送礼熔断走本地兜底文案、目标失败回 `action_result` —— 这块是全项目做得最好的部分，但**代码缺陷会被误标为 `llm_error`**（实测，§4.5） |

**给"假设每个功能都有 bug"这个前提的直接回答**：当前架构**没有**做到"任一功能坏掉只影响它自己"。最坏情形链（全部有代码依据，非猜测）：

```
某个子系统（如 SpeechDisplayRouter.Tick）确定性抛 NRE
  → OnUpdateTicked 在该行中断（无分段隔离）
  → 同 tick 后续全部停摆：礼物/对话/房客中继/聊天栏 4 个主线程队列不排水、
     WS 命令队列（execute_adjust / director_command）不执行、全部 agent 不 tick、决策队列不消费
  → SMAPI 每 tick（~60 次/秒）打一条带栈的 Error（ManagedEvent 兜底，游戏不崩）
  → SMAPI 日志被同一异常刷爆，真正的其它信号被淹没
  → 玩家侧表现：NPC 不动、不回应、送礼无反应；TS 侧 execute_adjust 10s 超时 → 30s 对账重发 → 无限循环
```

---

## 1. 审计范围与方法

**范围**（生产代码，测试/工具 mod 不计入门禁）：

| 部分 | 规模 |
|---|---|
| `src/ValleyAgent/`（C# 模组主体） | 147 文件 / 35,641 行 |
| `src/ValleyAgent.Abstractions/` | 49 文件 / 10,966 行 |
| `server/packages/core/src` + `server/packages/stardew/src` | 110 文件 / 23,552 行 |

**方法**（三条腿，互相校验）：

1. **静态规则扫描**：`scripts/check-exception-hygiene.mjs`，12 条规则（见 §9），对全仓产出 297 处命中，逐条人工复核过 Top 命中（误报已在规则里排除，例如 `Monitor.Log(msg, smapiLevel)` 这种间接传级别的不算）。
2. **实机探针**：`scripts/test/error-observability-probe.ts` 启动**真实** `startServer`（`llmCallOverride` 顶替真实 LLM），发 4 类畸形/边界帧，对照控制台输出与 `VALLEY_SERVER_LOGFILE` 落盘内容 —— §8 的全部结论都是实测，不是推演。
3. **关键路径人工审读**：tick 流水线、Harmony 补丁、WS 客户端三循环、初始化链、经济闭环（execute_adjust/adjust_result）、LLM provider 重试链、记忆/账本落盘、控制台命令。

**基线状态**：`cd server && bun test` = **580 pass / 0 fail**（本环境 bun 1.4.2 实测）。C# 侧本环境无 .NET SDK、无游戏 dll，**未编译未跑测试**；因此所有 C# 结论都是"代码事实 + 静态推理"，凡涉及运行时后果（崩不崩、日志刷多快）一律进 §7 猜想台账并附终验方案。

**判定基准**（项目自己的规矩，审计按它打分）：`AGENTS.md §3.6` —— *"降级是行为上的（不崩溃），日志是可观测性的（必须可见）。任何 LLM 调用必须 log 输入输出；任何决策分支必须 log 分支结果和原因；任何过滤/丢弃必须 log 被丢弃项和原因。"* 本次审计发现的多数 P0/P1 都是**违反了项目自己已经写下的这条铁律**。

---

## 2. 现状盘点：已有的好底子（修复时必须保留，不要推翻）

审计不只是挑毛病 —— 这套代码里已经有一批**做得对**的东西，它们是修复的模板：

| 已有能力 | 位置 | 评价 |
|---|---|---|
| 运行时错误落盘（双文件 + 5MB 轮转 + 自身异常全吞） | `Infrastructure/ModErrorLog.cs` | ✅ 设计正确。`ValleyAgent-error.log` + `ValleyAgent-server.log` 分发给朋友即可排障 |
| 进程级兜底钩子 | `ModEntry.cs:122-131`（`AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`） | ✅ 有。但 `UnobservedTaskException` 依赖 GC 终结才触发，**时机可能是几分钟后甚至永不**（§3.3） |
| 卡死取证仪器 | `Infrastructure/MainThreadWatchdog.cs` + `StuckOperationTracker` + `QueueTelemetry` | ✅ 全项目最强的可观测性投资：主线程停滞 >5s 自动 MiniDump + 伴随日志；队列深度 ≥100 告警 |
| 正确的 catch 范式 | `Multiplayer/MultiplayerEventRouter.cs:82-150`、`Multiplayer/HostRequestHandlers.cs`、`EventHandlerInitializer.OnUnsolicitedMessage` | ✅ `catch (Exception ex)` + `{ex}` 全栈 + Error 级 + 主线程队列纪律 —— **这就是应该推广到全仓的模板** |
| 经济原子批 + 幂等 + 回滚 | `Economy/AdjustExecutor.cs`（四阶段：静态校验→agent 解析→零副作用预校验→按序提交，失败回滚且回滚失败打 Error） | ✅ 逻辑严谨。**唯一缺口**：`Execute` 本身无外层 try（§3.9） |
| 回执超时不误回滚 + 对账重发 | `protocol-adapter.ts` `sendAdjust` / `scheduleReconcile` | ✅ 设计正确（避免双倍扣钱）。缺口：重发**无次数上限**（§3.9） |
| 降级可见（玩家侧） | `rule-engine.ts` 三档人设兜底 + `fallbackReason`（busy/billing/unavailable/llm_error）+ BUSY 灰字 + 送礼本地兜底文案 | ✅ 有品味。缺口：代码缺陷被归进 `llm_error`（§4.5） |
| 落盘原子性 | `atomic-fs.ts`（temp + rename）、`agent-memory.ts` / `agent-ledger.ts` 全部走它 | ✅ 2026-08-23 审计的成果，保住了 |
| 故障注入测试 | `server/packages/stardew/tests/fault-injection.test.ts`（CH-01..CH-06，19 例）、`log-tee.test.ts`、`server-ws-observability.test.ts` | ✅ TS 侧有意识。缺口：**没有一条测试断言"日志内容/级别"**，也没有 C# 侧的故障隔离测试（§6） |

---

## 3. P0：破坏"出错仍能跑"或"错误进不了日志"的缺口

### 3.1 `OnUpdateTicked` 是单点：397 行、10+ 子系统、零 try/catch

**事实**（扫描器 `CS-PUMP-NO-GUARD`，人工复核）：`Initialization/EventHandlerInitializer.cs:1788-2184`，方法体内 `try` 出现 **0** 次。串行调用顺序：

```
_transcriptSink.Drain() → SpeechDisplayRouter.Tick() → NPCGiftPatch.ProcessMainThreadActions()
→ ValleyAgentApi.ProcessMainThreadActions() → HostRequestHandlers.ProcessMainThreadActions()
→ DialogueBoxInputPatch.ProcessPendingReplies() → ChatBarRouter.ProcessPendingReplies()
→ ChatBarRouter.ProcessShoutReplies() → ProcessPendingMainThreadCommands() → ProcessPendingWsCommands()
→ ProcessPendingPreSpeakActions() → PlayerActionTracker.Sample() → 时间加速 → 决策批
→ TrySparkNearbyNpcs() → CheckInteractionIdleEviction() → foreach(agent) ProcessAgent()
→ 决策队列消费
```

**爆炸半径**：任何一处抛出 → 该行之后**全部**不执行。因为前 6 项是主线程队列泵、第 10 项是 WS 命令泵（`execute_adjust`/`director_command`/`allocate_agent`），后果是"网络对了也没法实际使用"（正是 2026-09-09 房客可用性修复要解决的那类症状），而这次是**全模式**（主机也一样）。

**SMAPI 兜底事实**（已核 SMAPI 源码 `Framework/Events/ManagedEvent.cs`）：每个 handler 调用被 try/catch 包住，异常时打 `"{mod} failed in the {EventName} event. Technical details:\n{stack}"` 到 **LogLevel.Error**，然后继续下一个 handler，**不会禁用该 handler**。所以：游戏不崩 ✅，但该 handler 本次 tick 的剩余逻辑全跳过 ❌，且下一 tick 重复抛出 → **每秒 ~60 条带栈 Error 日志**（量级为猜想，见 §7 C2）。

**修复建议**：分段安全执行 + 每段独立计时（现有 `drainStopwatch` 已在量总时长，改成按段量即可定位是哪一段挂了）：

```csharp
// 建议新增 Infrastructure/SafeRun.cs（与 QueueTelemetry 同目录同风格）
private void SafeRun(string segment, Action action)
{
    try { action(); }
    catch (Exception ex)
    {
        // 节流：同段 5s 内只打一次全栈，避免 60Hz 刷爆 SMAPI 日志（复用 QueueTelemetry.ShouldWarn）
        if (QueueTelemetry.ShouldWarn($"tick-{segment}", 5000))
        {
            _monitor.Log($"[Tick] segment '{segment}' failed (后续段仍会继续执行): {ex}", LogLevel.Error);
            ModErrorLog.LogError("TickSegment", $"segment '{segment}' failed", ex);
        }
    }
}
// OnUpdateTicked 内：SafeRun("speech-router", SpeechDisplayRouter.Tick);
//                    SafeRun("gift-pump", NPCGiftPatch.ProcessMainThreadActions); ... 
```

**验证判据**：单测注入"第 2 段抛异常"，断言第 3..N 段仍被调用 + 只落 1 条 Error（节流生效）。

---

### 3.2 8 处 Harmony 补丁入口无外层守卫 —— 异常直接注入游戏原生调用栈

**事实**（扫描器 `CS-HARMONY-NO-GUARD`）：

| 文件:行 | 方法 | 注入的游戏调用栈 | 方法体 |
|---|---|---|---|
| `Patches/NPCDialoguePatch.cs:62` | `Prefix` | `NPC.checkAction`（**每次右键村民**） | 110 行 / 0 catch |
| `Patches/NPCDialoguePatch.cs:277` | `CloseDialoguePostfix` | `DialogueBox.closeDialogue` | 40 行 / 0 catch |
| `Patches/NPCGiftPatch.cs:84` | `Prefix` | `NPC.tryToReceiveActiveObject`（**每次送礼**） | 238 行 / 4 catch（均在内部子块） |
| `Patches/NPCGiftPatch.cs:366` | `Postfix` | 同上 | 201 行 / 4 catch（同上） |
| `Patches/DialogueBoxInputPatch.cs:159` | `DrawPostfix` | `DialogueBox.draw`（**对话框开着时每帧**） | 26 行 / 0 catch |
| `Patches/DialogueBoxInputPatch.cs:196` | `ReceiveKeyPressPrefix` | `DialogueBox.receiveKeyPress`（**每次按键**） | 61 行 / 0 catch |
| `Patches/DialogueBoxInputPatch.cs:262` | `ReceiveLeftClickPrefix` | `DialogueBox.receiveLeftClick` | 14 行 / 0 catch |
| `Patches/ChatBoxInputPatch.cs:26` | `ReceiveChatMessagePrefix` | `ChatBox.receiveChatMessage`（**每条聊天消息**） | 27 行 / 1 内部 catch |

**对照组**：`Patches/SocialPagePatch.cs:36` 与 `:70` **有**外层 `try { ... } catch (Exception ex) { Monitor?.Log(..., LogLevel.Error); }` —— 说明正确范式在仓库里已经存在，只是没有一致应用。

**为什么这是 P0**：Harmony 不捕获补丁异常，SMAPI 的 `ManagedEvent` 兜底也覆盖不到（那只包 SMAPI 事件）。异常会出现在**游戏自己的**渲染/输入/交互调用栈里：轻则该帧渲染失败或该次交互无响应，且 SMAPI 日志里以"游戏本体"面目出现（**归因错误** —— 排查者看不到是哪个子系统抛的）；重则崩溃到桌面（此时只有 `ModEntry` 注册的 `AppDomain.UnhandledException → ValleyAgent-error.log` 留现场）。是否真会崩到桌面属猜想，见 §7 C1。

**具体高危点**：`DialogueBoxInputPatch.SubmitInput()`（由 `ReceiveKeyPressPrefix` 调用）在 **try 之外**执行
```csharp
var worldSnapshot = WorldSnapshotBuilder.Build(npcName, GetNpcState(npcName));  // line 323
var playerId = Game1.player.UniqueMultiplayerID.ToString();                     // line 324
```
而 `WorldSnapshotBuilder.Build` 的 XML 注释自己写着"调用方需保证 `Game1.currentLocation` / `Game1.player` 已初始化"，方法体内**零 catch**（`AI/WorldSnapshotBuilder.cs`）。玩家在过场/淡入淡出/切图瞬间敲回车 → NRE 直接飞进 `DialogueBox.receiveKeyPress`；更糟的是 `_isWaitingForResponse` 已在 line 316 置 `true` 且只有 `ProcessPendingReplies` 出队时才复位 → **该对话框的输入永久死锁**（本轮会话再也发不出话）。

**修复建议**：所有补丁入口统一"外层守卫 + 失败回落原版"：
```csharp
private static bool Prefix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
{
    try { return PrefixCore(__instance, who, l, ref __result); }
    catch (Exception ex)
    {
        Monitor?.Log($"[Patch] NPCDialoguePatch.Prefix failed for {__instance?.Name} — falling back to vanilla: {ex}", LogLevel.Error);
        ModErrorLog.LogError("HarmonyPatch", "NPCDialoguePatch.Prefix", ex);
        return true;   // 关键：放行原版，功能坏掉但游戏照常
    }
}
```
`SubmitInput` 的快照采集必须移进 try，且 `catch` 里 `_isWaitingForResponse = false` + 给玩家一句灰字（沿用 `fallbackReason` 语义）。

---

### 3.3 WebSocketClient 三个长跑循环捕获类型不全 —— 意外异常让重连/心跳**永久**死亡

**事实**（扫描器 `CS-LOOP-CATCH-GAP`）：`Abstractions/WebSocket/WebSocketClient.cs`

| 行 | 方法 | 现有 catch | 后果 |
|---|---|---|---|
| 147 | `ReadLoopAsync` | `OperationCanceledException`、`WebSocketException` | 其它异常穿透 → `finally` 仍会 `FailAll` + 触发重连（相对安全），但异常本身变成**未观测 Task 异常**，只能等 GC 终结时经 `TaskScheduler.UnobservedTaskException` 落盘（可能几分钟后，也可能永不） |
| 230 | `ReconnectIndependentAsync` | `OperationCanceledException`、`WebSocketException` | **最严重**：`CreateClientWebSocket`/`ConnectAsync`/`SendUnsafeAsync`/`FlushOutboxAsync` 抛任何别的类型（`InvalidOperationException`「ws 已连接/已中止」、`ObjectDisposedException`、`UriFormatException`、`HttpRequestException`）→ while 循环整体退出，`finally` 只把 `_reconnecting` 复位，**再没有任何人会调用 ReconnectIndependentAsync**（没有周期探活）→ 本局游戏内 TS 连接永久断开，日志里只有 GC 时机不定的一条 UnobservedTask |
| 305 | `HeartbeatLoopAsync` | `OperationCanceledException`、`WebSocketException` | `SendUnsafeAsync` 撞上并发 Abort/Dispose 会抛 `ObjectDisposedException`/`InvalidOperationException` → 心跳循环静默死亡 → 半开连接（TCP 还在、对端已死）不再被检测 → 之后每次对话都等满 **120s** 超时（`PendingRequestTracker.WaitForResponseAsync` 默认 120s） |

另：`PendingRequestTracker.FailAll`（`PendingRequestTracker.cs:88-95`）用 `SetException` 而非 `TrySetException`，与 `CompleteRequest` 竞态时会抛 `InvalidOperationException`，而它是在 `ReadLoopAsync` 的 `finally` 里被调用的 —— finally 抛出会吞掉原始异常。

**修复建议**：三循环统一 `catch (Exception ex)` 兜底 + 记 Error + 循环内 `continue`（重连循环必须"永不退出"）；`FailAll` 改 `TrySetException`；额外加一条**周期探活**（`OnUpdateTicked` 每 ~10s 检查 `IsConnectedAsync`，断了就重新 `ConnectAsync`），把"重连循环已死"从不可恢复变成可恢复。

---

### 3.4 初始化失败无降级：一次异常 = 整个模组半死，且日志只有一行 message

**事实链**：

1. `ModEntry.cs:252-311` `OnSaveLoadedInitialize`（catch 在 305-311）：
   ```csharp
   catch (Exception ex)
   {
       Monitor.Log($"Failed to initialize ValleyAgent: {ex.Message}", LogLevel.Error);
       throw;                     // ← 重抛
   }
   ```
   → SMAPI 记一条带栈 Error（好），但 `_initialized` 仍为 false、`_container` 半建、事件/补丁可能只注册了一半，**没有任何回滚或降级**（例如退回 `Inert` 模式让原版体验完好）。玩家看到的现象是"装了 mod 但完全不工作"，且 `ReturnedToTitle`→再进存档会重复这条路径。
2. `Initialization/ServiceInitializer.cs:65-493` `Initialize()`：~50 个服务顺序 new + 注册，**没有分段隔离**。任何一个（`ValleyTalkBioLoader.Load()` 读损坏 JSON、`GameSummaryLoader`、`NpcEconomyProfileLoader`、`LlmConfigWriter`、`Directory.CreateDirectory` 权限问题）抛出 → 整个初始化中断，且**已注册的服务留在容器里**（半初始化状态）。
3. `EventHandlerInitializer.cs:344-386` `RegisterHarmonyPatches`：
   - `catch` 只捕 **`TargetInvocationException`**。Harmony 的 `PatchAll()` 实际抛的是 `HarmonyLib.HarmonyException`（内部才包 `TargetInvocationException`/编译异常）→ **捕不到，直接逃逸**到第 1 条的 rethrow 路径。而且 `HarmonyException.Message` 不是有价值的部分（真正线索在 `GetInstructionsWithOffsets()` / `InnerException`），即使捕到也应打全栈。
   - 该方法把"静态依赖注入 + `ChatBarRouter.Initialize` + `harmony.PatchAll()`"混在一个 try 里：若 `ChatBarRouter.Initialize` 抛出，`PatchAll()` 根本不会执行 → 静态字段已赋值但补丁未打（不一致状态）；若 `PatchAll()` 中途失败，**已打的补丁没有 `UnpatchAll` 回滚** → 游戏带着半套补丁运行。

**修复建议**：
- `ServiceInitializer.Initialize()` 按"可选子系统"分段（RAG 数据 / 经济档案 / 联机接线 / 渲染 / 导演工具），每段 `try/catch` + 失败登记到一张 `DegradedFeatures` 表；核心段（配置、容器、WS）失败才整体放弃。
- 失败时**不要 rethrow**，改为落 `Inert`/降级模式 + 一条 `LogLevel.Alert` 或 `Error` 的玩家可读提示（"ValleyAgent 初始化失败，本次游戏已回退原版体验；详见 Mods/ValleyAgent/ValleyAgent-error.log"）+ `ModErrorLog.LogError(..., ex)` 全栈。
- `RegisterHarmonyPatches`：`catch (Exception ex)`；把 DI 与 `PatchAll` 拆成两段；`PatchAll` 失败时 `harmony.UnpatchSelf()` 回滚；日志附 `ex.ToString()` 与（若是 HarmonyException）`GetInstructionsWithOffsets()`。

---

### 3.5 TS → C# 的错误回包：协议里没有、不带 requestId、字段名两端不一致

**事实**（实测 + 代码对照）：

| 端 | 代码 | 事实 |
|---|---|---|
| TS 发送 | `server.ts:180-190` | `ws.send(JSON.stringify({ type: "error", message: String(err) }))` —— **不带 requestId**；`String(err)` 只有 `"TypeError: xxx"`，无栈 |
| 协议 | `server/protocol/messages.json` | 全文**没有 `error` 这个消息类型**（单一事实源里不存在）→ `check:protocol` 永远查不到这条链路 |
| C# 读取（请求-响应路径） | `WebSocketClient.cs:76-85` `CheckErrorResponse` | 读的是 `root.TryGetProperty("error", ...)` —— **字段名对不上**（TS 发的是 `message`）→ 恒为 `"Server error: Unknown error"` |
| C# 读取（无 requestId → 走 unsolicited） | `WebSocketClient.cs:198-215` → `EventHandlerInitializer.cs:1291-1320` `OnUnsolicitedMessage` 的 `default:` | `_monitor?.Log($"[WS] Unhandled unsolicited message type: {type}")` —— **未给 level = SMAPI 默认 Trace = 控制台默认不可见**，而且**只打 type、不打 message 内容** |

**净效果**（探针实测，§8 探针 2/3）：TS 侧 handler 抛错时，游戏侧**既不会快速失败也看不到原因**——pending 请求因为回包没有 requestId 而匹配不上，只能等满 120s 超时（`TimeoutException` → 玩家看到"（Haley 在思考...）"）；那条真正带原因的 `error` 帧被当作"未处理的 unsolicited 消息"以 Trace 级丢弃，连 `message` 字段都没读。

**修复建议**（一次改齐，四处联动）：
1. `messages.json` 补 `error` 消息 schema（`requestId`（可空）、`code`、`message`、`stack?`、`npcName?`），让 `check:protocol` 把它纳入契约。
2. `server.ts` 回包带 requestId：`{ type:"error", requestId: parsed?.requestId, code: classify(err), message: errMessage, stack: errStack }`；`JSON.parse` 失败时单独走 `code:"badJson"` 并把原文前 200 字符带上（当前畸形帧的原因完全不可见）。
3. C# `CheckErrorResponse` 同时读 `message` 与 `error`（向后兼容），并把 `code` 带进异常消息。
4. `OnUnsolicitedMessage` 增加 `case "error"`：打 **LogLevel.Error** + 全文 + `ModErrorLog.LogError("ServerError", ...)`。

---

### 3.6 落盘日志把错误详情销毁：`JSON.stringify(Error) === "{}"`（实测）

**事实**：`log-tee.ts` 的 `serializeArg()`：
```ts
function serializeArg(arg: unknown): string {
  if (typeof arg === "string") return arg;
  try { const s = JSON.stringify(arg); if (s !== undefined) return s; } catch { /* 循环引用兜底 */ }
  return String(arg);
}
```
`Error` 的 `message`/`stack` 是**不可枚举**属性 → `JSON.stringify(new Error("boom"))` 得到 `"{}"`（不是 `undefined`）→ 永远走不到 `String(arg)` 兜底。

**为什么是 P0**：发行包默认 `ServerConsoleWindow=true`（`ServerProcessManager.cs:304-326`），该模式下 C# **不重定向 stdout**，`ValleyAgent-server.log` 由 TS 自己 tee —— 也就是说 **tee 文件是唯一的持久化现场**（cmd 窗口关掉/整机卡死就没了，这正是 2026-09-11 加 tee 的初衷）。而 13 处 `console.error(..., err)`（扫描器 `TS-ERR-OBJECT-TO-TEE`）恰好是全部最重要的错误出口：

```
cli.ts:93   unhandledRejection: reason      cli.ts:96   uncaughtException: err
cli.ts:147  fatal: err（启动失败唯一线索）   server.ts:182  message handler error: err
server.ts:189 failed to send error response  protocol-adapter.ts:681  [dialogue] LLM failed for {npc}: err
protocol-adapter.ts:354/457/579/653/694/698  ledger.save / memory.save 失败 ×6
```

**实测输出**（§8）：
```
控制台（ ephemeral ）：TypeError: undefined is not an object (evaluating 'req.agents.length')  at handleReconnectSync (...:228)
落盘文件（ persistent ）：[2026-09-15T13:47:38.853Z] [error] [server] message handler error: {}
```
即：**玩家把 `ValleyAgent-server.log` 发回来，我们什么也看不到**。这条单独就足以让"log 中突出有价值的错误信息"不成立。

**修复建议**（一处改动，13 个出口全部受益）：
```ts
function serializeArg(arg: unknown): string {
  if (typeof arg === "string") return arg;
  if (arg instanceof Error) return `${arg.name}: ${arg.message}\n${arg.stack ?? "(no stack)"}`;   // ← 关键
  try { const s = JSON.stringify(arg, errorReplacer); if (s !== undefined) return s; } catch {}
  return String(arg);
}
```
配套：`log-tee.test.ts` 增加断言"`console.error("x:", new Error("boom"))` 落盘必须含 `boom` 与 `at `"（现有测试只覆盖了字符串参数，所以这个洞一直没被测到）。

---

### 3.7 `routeMessage` 的 `default:` 静默 ack —— 协议漂移零可观测（实测）

**事实**：`protocol-adapter.ts:733-752`
```ts
const m = msg as { type: string };
switch (m.type) { ... default: return { type: "ack", requestId: "unknown" }; }
```
- 未知类型 → 静默 ack，**零日志**（扫描器 `TS-UNKNOWN-TYPE-SILENT`；探针 1 实测：日志文件里完全找不到这条消息的任何痕迹）。直接违反 `AGENTS.md §3.6` 铁律"任何过滤/丢弃必须 log 被丢弃项和原因"。`consolidate_day` 2026-09-14 刚被降级为 planned（C# 发送端已删）—— 这类"一端还在发、另一端已删"的漂移正是本分支要防的。
- `msg` 为 `null`/非对象 → `m.type` 抛 `TypeError`（探针 2 实测），落进 §3.5/§3.6 的双重黑洞。
- `requestId: "unknown"` 还会污染 C# 侧：C# 收到带 `requestId:"unknown"` 的帧会去匹配 pending（匹配不上）→ 又落进 `OnUnsolicitedMessage` 的 Trace 级 default。

**修复建议**：`default` 分支 `console.warn` 打 `type` + 原文前 200 字符 + 已知类型清单（并做**每类型一次**的去重计数，避免刷屏）；`routeMessage` 开头做最小结构校验（`typeof msg === "object" && msg !== null && typeof (msg as any).type === "string"`），不合法直接回 `code:"badFrame"` 的 error 帧。

---

### 3.8 `Agent.prompt` 的 `.then()` 无 `.catch()` —— NPC 可被永久锁死为 BUSY

**事实**：`core/src/agent.ts:88-100`（扫描器 `TS-THEN-NO-CATCH` 全仓唯一命中）
```ts
stream.awaitAll().then((allEvents) => {
  for (const event of allEvents) { for (const sub of this.subscribers) sub(event); ... }
  ...
  this.activeRun = null;               // ← 只有 then 分支会清
});                                    // ← 没有 .catch
```
**失效链**：任一订阅者抛出（或 `awaitAll` 自身 reject）→ `activeRun` 永不清空、`proxyStream` 永不 `done()` → `StardewAgent.runOnce` 的 `await stream.awaitAll()` **永久挂起** → `handleDialogue` 的 `finally { releaseLock() }` 永不执行 → 该 NPC 之后所有对话都在 `acquireLock(15s)` 后返回 BUSY 灰字，**直到重启 TS 服务器**。同时这个 rejection 只能靠 `cli.ts` 的全局 `unhandledRejection` 兜住，而它写进日志的是 `{}`（§3.6）。

**已实测复现**（`scripts/test/agent-wedge-repro.ts`，bun 1.4.2）：
```
proxy stream  : awaitAll-HUNG (>1s)
agent.isIdle(): false        ← activeRun 永不清空
errorMessage  : null         ← 错误连 AgentState 里都看不见
二次 prompt   : 抛错 → Agent wedge-test is already running
```
如实标注边界：生产代码里现有两个订阅者（`ConsoleLogSubscriber.onEvent`、`RunTranscriptRecorder`）内部都有 try/catch 自防，所以这是**已实测可达、但当前尚未被现场触发**的卡死面 —— 任何一次新增订阅者（统计/审计/UI 推送）忘了自防就会引爆，且引爆后**只能重启 TS 服务器**恢复。

**修复建议**：`.then(..., onError)` 或 `.catch()`：清 `activeRun`、`proxyStream.emit({type:"error"})` + `done()`、`console.error` 带全栈。配套给 `handleDialogue` 加**服务端超时**（例如 `Promise.race([agent.runDialogue(...), timeout(90s)])`，90s < C# 的 120s），让"任何原因的挂起"都能自愈并回 fallback，而不是靠 C# 超时。

---

### 3.9 `execute_adjust` 在 C# 侧抛异常 → 没有回执 → TS 无限对账重发

**事实链**（三段代码对接，无一处推测）：
1. `Economy/AdjustExecutor.cs:73-97` `Execute()` **无外层 try/catch**（`ExecuteCore` 里 4 个阶段全靠 `TryXxx` 返回码；`TryCommit` 内部碰 `Game1.player`/物品数据/`AgentInventory`，NRE 完全可能）。
2. `EventHandlerInitializer.cs:1387-1409` `HandleExecuteAdjust` 的 `catch (Exception ex)` 只打一条 **Warn**、`ex.Message`、**不含 instructionId**，并且**不回 `adjust_result`**。
3. TS `protocol-adapter.ts:343-413` `sendAdjust`（10s 超时，故意不回滚账本，挂 30s 对账）→ `:415-431` `scheduleReconcile` 重发 → C# 再次抛 → 再超时 → 再重发……**没有次数上限**，每 ~40s 一轮，直到重启。可见痕迹只有一条 `console.log`（`[execute_adjust] <id> reconciling after timeout`），账本 `pending` 永久挂着，工具侧收到的是 `failureCode:"internalError"` + `detail:"receipt timeout ... do not repeat payment"`。

**净效果**：一个 C# 端的普通缺陷，会表现为"经济系统无限重试 + 日志里没有任何 ERROR + 账本永远 pending"。

**修复建议**：
- `AdjustExecutor.Execute` 外层 `try/catch (Exception ex)` → 合成 `success:false, failureCode:"internalError", detail: ex.GetType().Name + ": " + ex.Message` 的回执（**异常也必须回执**，这是"每次资产变动都有指令+回执闭环"原则的必然要求）+ `ModErrorLog.LogError("AdjustExecutor", instructionId, ex)`。
- `HandleExecuteAdjust` 的 catch：带 `instructionId` 打 **Error**（不是 Warn）+ 全栈，并兜底发一条 `internalError` 回执（与上一条互为双保险）。
- TS `scheduleReconcile` 加**重发上限**（如 3 次）与指数退避；超限后 `console.error` + 账本 `pending` 标 `needsManualReconcile`（不再静默无限转）。

---

## 4. P1：错误信息的"价值"被系统性削弱

### 4.1 139 / 159 个会记日志的 catch 只记 `ex.Message`（丢异常类型 + 丢栈）

**实测统计**（扫描器 `CS-LOG-NO-STACK`）：生产 C# 代码 237 个 catch 块中，159 个会记日志，其中**只有 21 个**带完整异常（`{ex}` / `ToString()`），**139 个（87%）**只写 `ex.Message`。

分布 Top：`EventHandlerInitializer.cs` 32、`CommandExecutor.cs` 11、`ModEntry.cs` 10、`Api/DialogueManagementApi.cs` 9、`Patches/NPCGiftPatch.cs` 8、`Multiplayer/*` 12、`WebSocketClient.cs` 5。

**为什么致命**：.NET 里最常见的缺陷类是 `NullReferenceException`，它的 `Message` 是 **"Object reference not set to an instance of an object."** —— 没有类型名、没有栈、没有任何定位信息。玩家/朋友发回日志时，我们看到的就是一行无法行动的句子。对照 `MultiplayerEventRouter.cs:147` 的正确写法：`$"Failed to handle {e.Type}: {ex}"`（`{ex}` = 类型 + message + 栈）。

**修复建议**：批量把 catch 内日志的 `{ex.Message}` 改成 `{ex}`（Warn 级可保留 message + 追加 `ex.GetType().Name`，Error 级必须全栈），并统一一条约定写进 `AGENTS.md §5`：**"catch 里的日志必须带 `{ex}`；只有明确知道异常语义且不需要栈时才允许 `ex.Message`"**。扫描器已把这条做成规则，改完跑 `--update-baseline` 收紧即可。

### 4.2 47 处 `Monitor.Log` 未给 level → SMAPI 默认 **Trace** → 控制台默认不可见

**依据**：SMAPI `IMonitor.Log(string message, LogLevel level = LogLevel.Trace)`（已核 SMAPI 源码 `src/SMAPI/IMonitor.cs`），且官方文档明确 *"Trace messages won't appear in the console window by default"*。

**其中真正是"错误/丢弃信号"的 13 处**（其余 34 处是决策留痕，Trace 尚可接受）：

| 位置 | 内容 | 为什么必须是 Warn/Error |
|---|---|---|
| `EventHandlerInitializer.cs:1315` | `[WS] Unhandled unsolicited message type: {type}` | **TS 的 error 帧就落在这里**（§3.5）—— 唯一的服务端错误线索被设为不可见 |
| `EventHandlerInitializer.cs:919` | `Structured save data load skipped: {sEx.Message}` | 存档数据被跳过 = 数据丢失信号 |
| `MultiplayerEventRouter.cs:108` / `:141` | `Unknown message type: {e.Type}` | 联机协议漂移（主机/房客版本不一致）—— 正是 §3.7 同类问题 |
| `FarmhandDialogueTransport.cs:122` / `:134` | `Response requestId ... has no pending` / `No pending request ..., dropping response` | **房客收不到回复的唯一线索**（2026-09-09 C 系列问题域） |
| `FarmhandGiftTransport.cs:111` / `:122` | 同上（送礼） | 同上 |
| `AgentSyncBroadcaster.cs:211` | `BroadcastImmediateState: agent '{npcName}' not found` | 联机状态同步丢包 |
| `AgentTickLoop.cs:432` | `checkSchedule for target lookup failed: {ex.Message}` | **catch 里的异常**却打 Trace（双重降级：丢栈 + 不可见） |
| `ChatBarRouter.cs:261` | `[ChatBar] Dropped trivial message: {text}` | 丢弃玩家输入 |
| `StateFeasibility.cs:51/61/81/100/109/118` | `cannot FARM/MINE/FORAGE/FIGHT/TALK at ...` | "NPC 为什么不做某事"的决策分支 —— `AGENTS.md §3.6` 铁律要求可见（6 处） |

**修复建议**：这 13 处显式提到 `LogLevel.Warn`（丢弃/未知类型）或 `LogLevel.Debug`（决策分支，Debug 在控制台可见且不至于刷屏）；扫描器 `CS-LOG-DEFAULT-LEVEL` 长期把"未给 level"压到 0。

### 4.3 50 处 catch 既不记日志也不重抛（需分诊，不是全都要改）

扫描器 `CS-SILENT-CATCH` 50 处，人工分诊：

- **可以接受（约 22 处）**：`OperationCanceledException`（断连/取消，语义即"正常"）、标题屏/测试环境的 `NullReferenceException` 守卫（`GameContextSyncBuilder` ×7、`DirectorContextBuilder` ×2、`AgentService.cs:318`、`AdjustExecutor.cs:760`）、`ModErrorLog`/`MainThreadWatchdog` 自身（已在规则白名单）。
- **必须补日志（约 12 处，都是"静默降级改变行为"）**：
  | 位置 | 静默后果 |
  |---|---|
  | `Economy/NpcEconomyProfileLoader.cs:96` / `:100` | `catch (IOException/JsonException) { _profiles.Clear(); }` —— **单个文件读失败就清空整张经济档案表**，全部 NPC 退回默认钱包/性格，零日志 |
  | `Config/NpcConfigLoader.cs:111` / `:115` | 单个 NPC 配置坏 → 跳过该 NPC（行为差异不可解释） |
  | `RAG/GameSummaryLoader.cs:359` / `:395`、`RAG/ValleyTalkBioLoader.cs:368` | 数据文件 JSON 坏 → `return null`（人设/RAG 静默缺失，直接打击 P0 目标"真实性格"） |
  | `Save/SaveDataManager.cs:101` | 存档 JSON 坏 → 返回默认存档数据（**玩家进度静默归零**） |
  | `Chat/ChatBarRouter.cs:77` | `catch (Exception) { return string.Empty; }` —— 聊天路由里吞掉一切异常 |
  | `Chat/ChatRouteResolver.cs:278` | `catch (Exception) { return 0.5; }` —— 评分函数吞异常返回中位数 |
  | `i18n/TranslationProvider.cs:177` | 翻译表 JSON 坏 → 空字典（全 mod 文案变 key） |
  | `Multiplayer/Transports/FarmhandDialogueTransport.cs:174` | `ParseActions` 解析失败 → `return null`（房客侧动作静默消失） |
  | `Protocol/StateChangedSender.cs:53` | 状态变更通知失败全吞 → TS 的 `actualState` 镜像静默漂移 |
  | `WebSocket/ServerProcessManager.cs:705` / `:715` | 杀残留进程失败静默（接着就是端口占用启动失败，因果断链） |
- **建议**：这一批统一补"降级留痕"最小集 —— `Monitor.Log($"[X] degraded: <原因>（影响：<后果>）", LogLevel.Warn)`，并在注释里写清"为什么可以不抛"。数据文件类（RAG/经济档案/存档/翻译）额外要求：**损坏文件改名隔离**（`.corrupt-<ts>`）而不是就地重建。

### 4.4 TS 侧 16 处"失败/丢弃"事件打在 `console.log`（落盘级别 `[log]`，无法按级别 grep）

扫描器 `TS-FAIL-AT-LOG-LEVEL` 命中（全部人工确认语义为失败/降级）：

```
core/llm-provider.ts:246   [llm] retry n/m (HTTP xxx: ...)         ← 重试
core/llm-provider.ts:252   [llm] unavailable after N retries        ← LLM 彻底不可用（最该是 ERROR）
stardew/console-log-subscriber.ts:78  [turn] {npc} error: {msg}      ← Agent 级错误（且丢 ev.error 的栈）
stardew/protocol-adapter.ts:195/213/231/273  dropped: ... not wired / 未配置   ← 消息被丢弃 ×4
stardew/protocol-adapter.ts:296  business-check failed: {reason}
stardew/protocol-adapter.ts:349  {instruction} failed: {reason}
stardew/protocol-adapter.ts:421  reconciling after timeout          ← §3.9 的无限重发痕迹
stardew/protocol-adapter.ts:445  no pending waiter for ...          ← 回执孤儿
stardew/protocol-adapter.ts:551  {npc} BUSY (locked, waited Nms)
stardew/protocol-adapter.ts:700  [send] dialogue npc=X FALLBACK     ← 玩家实际看到的降级
stardew/stardew-agent-registry.ts:141/154  [lock] busy / still busy, giving up
```

**后果**：`log-tee` 落盘的行首级别标记是 `[log]`，排障时 `grep -i "error\|warn" ValleyAgent-server.log` **一条都抓不到**；"LLM 彻底不可用"和"一次正常对话"在文件里级别相同。

**修复建议**：失败/丢弃/降级统一 `console.warn`，不可用/数据损坏/回执孤儿统一 `console.error`；`ConsoleLogSubscriber` 的 `error` 事件改 `console.error` 并带上 `ev.error` 的栈（事件里已经保留了原始 error 对象，只是没打出来）。同时给 `log-tee` 的行加一个统一的可 grep 前缀（例如 `[ERROR]`），并在 `AGENTS.md §5` 写死级别约定。

### 4.5 玩家侧降级原因被误标：代码缺陷 → `fallbackReason: "llm_error"`（实测）

**事实**：探针 4 发送缺 `worldSnapshot` 的 dialogue → `decodeWorldSnapshot` 抛 `TypeError: undefined is not an object (evaluating 'snap[field]')`（**这是 TS 自己的契约/代码缺陷**）→ `rule-engine.ts:9-28` 的 `buildFallbackResponse` 只区分 `LLMBillingError` / `LLMUnavailableError`，其余一律 `llm_error` → 回包 `fallbackReason:"llm_error"`，C# 打 `[Chat] Haley: fallback (reason=llm_error)`（而且是 **Debug** 级）。

**后果**：排障者会被引导去查 API key / 额度 / 网络，而真正的原因是协议字段缺失或 TS 代码 bug。这是"错误信息不**正确**"的典型（用户问题里的"正确的"三个字）。

**修复建议**：`fallbackReason` 扩档 —— `bad_request`（快照/字段校验失败，含缺失字段名）、`internal_error`（非 LLM 类异常）、`validation_failed`（`OutputValidator` 重试仍不合格，当前也被归进 `llm_error`）；`messages.json` 同步取值约定；C# 侧把 fallback 日志提到 `Warn` 并原样打出 reason。

### 4.6 数据损坏 = 静默重建 + 覆盖原文件（记忆与账本不可逆丢失）

- `agent-memory.ts:506-509`：`load()` 失败（非 ENOENT）→ `console.warn` + 以空记忆继续 → 下一次 `save()` 用**空数据原子覆盖**损坏文件 → NPC 的全部长期记忆/好感**不可逆丢失**，日志只有一行 warn（且 message-only）。
- `agent-ledger.ts:299-305`：同型；账本是**权威经济记录**，损坏后 `getOrCreate` 建默认（money 0 / seeded false），再由 worldSnapshot 重新播种 —— 自愈合理，但"曾发生过损坏"必须是 **ERROR** 级且留下证据。
- `npc-prompt-loader.ts:33-36`：构造函数 `readFileSync` + `JSON.parse` **零守卫** → 人设文件缺失/损坏 = `startServer` 抛出 = `cli.ts` `fatal:` + `process.exit(1)`；在 `ConsoleWindow=true` 模式下 cmd 窗口随进程秒退而关闭，落盘日志里那行 `fatal: {}`（§3.6）就是全部线索；C# 侧只能报 `Server process exited prematurely with code 1`。
- `npc-prompt-loader.ts:59-67` `getPhasePrompt`：NPC 不在数据文件里 → 静默返回 `"你是 X，一个星露谷的居民。"` —— SVE/其它内容包 NPC 会拿到无人设 prompt，**零日志**（直接打击 P0 目标"真实性格"，且完全不可观测）。

**修复建议**：①损坏文件先 `rename` 成 `<name>.corrupt-<ISO>.json` 再重建，并 `console.error` 全量；②`NpcPromptLoader` 构造包 try/catch，失败时打**含路径 + 原始错误**的 fatal 日志再退出（并考虑：文件坏但可降级 → 用内置最小人设继续跑，让游戏至少能玩）；③`getPhasePrompt` 首次命中兜底时 `console.warn` 一次（按 NPC 去重），并在启动时打印"数据文件覆盖 N 个 NPC，缺失清单：..."。

### 4.7 熔断器没接主对话路径 → 最主要的失效模式下它永远不会打开

**事实**：`CircuitBreaker`（501 行，含 `OnStateChanged`/`OnFallbackActivated` 事件，`ServiceInitializer.cs:132-135` 已订阅并打 Info/Warn）的 `RecordFailure` 调用点只有三处：
- `Api/DialogueManagementApi.cs:291`（**给其它 mod 用的 API**，不是游戏内对话路径）
- `Debug/ChatHandler.cs:127`（SMAPI 控制台 `ValleyAgent_chat` 命令）
- `Patches/NPCGiftPatch.cs:562`（送礼路径）

而玩家实际用的两条对话路径 —— `DialogueBoxInputPatch.SubmitInput`（对话框）与 `ChatBarRouter.SendDialogueAsync`（聊天栏）—— **既不调 `RecordFailure` 也不调 `RecordSuccess`**。

**后果**：LLM 长期不可用/服务器假死时，熔断器始终保持 CLOSED，`NPCGiftPatch`/`ChatHandler` 里那些"circuit breaker OPEN → 本地兜底文案"的保护分支对主路径无效；`ValleyAgent_status` 与日志里的 `Circuit Breaker: CLOSED` 会给出**误导性的健康信号**（看起来一切正常）。

**修复建议**：两条主对话路径的成功/失败/耗时统一喂熔断器（`RecordSuccess` / `RecordFailure(reason)` / `RecordResponseTime`），失败原因用 §4.5 扩档后的 `fallbackReason`；`ValleyAgent_status` 增加"最近 N 次对话成败 + 熔断状态 + 上次失败原因"。

### 4.8 队列泵只捕窄类型（5 处）—— NRE 直接穿透到 §3.1 的单点

扫描器 `CS-PUMP-NARROW-CATCH`：
```
EventHandlerInitializer.cs:967   OnSaving                        只捕 InvalidOperationException
EventHandlerInitializer.cs:1071  OnDayStarted                    只捕 InvalidOperationException
EventHandlerInitializer.cs:1733  ProcessPendingMainThreadCommands 只捕 InvalidOperationException
EventHandlerInitializer.cs:1764  ProcessPendingPreSpeakActions   只捕 InvalidOperationException / ArgumentException
Patches/NPCGiftPatch.cs:65       ProcessMainThreadActions        只捕 InvalidOperationException
```
对照组：`HostRequestHandlers.ProcessMainThreadActions` 用的是 `catch (Exception ex)` + `{ex}` + 注释"队列排水无外部兜底，吞异常保帧，但必须留痕" —— **同一种泵，两种标准**。

其中 `OnSaving` 尤其要紧：它遍历全部 agent 序列化存档数据，`catch` 只捕 `InvalidOperationException`；任一 agent 的 `Inventory`/`Brain` 为 null 抛 NRE → 逃出 → **全部 agent 的存档数据这一局都不写**（且没有 per-agent 隔离，一个坏 agent 连带所有 agent）。

**修复建议**：所有"队列泵/存档遍历"改 `catch (Exception)`；`OnSaving` 加 per-agent try/catch（坏的那个跳过并记 Error，其余照常存）。

### 4.9 控制台命令（SMAPI CLI）：只捕 2 类异常 + 失败降 Warn + 没有健康诊断命令

- `Debug/ConsoleCommands.cs:137-160` `Execute()`：只捕 `InvalidOperationException` / `ArgumentException`，其它异常（NRE、`JsonException`、`TaskCanceledException`）逃逸到 SMAPI 的命令执行器。
- `EventHandlerInitializer.cs:396-410`：命令失败（`result.Success == false`）统一打 **`LogLevel.Warn`** —— 真正的错误在 CLI 里被降级显示。
- 命令集共 13 个（`ValleyAgent_status/agents/context/test_llm/reset_circuit/token_usage/debug/allocate/deallocate/chat/force_decision/reload_config/help`），**没有一个能回答"现在到底哪里坏了"**：WS 连接状态、TS 进程是否存活、`ValleyAgent-server.log` 最后 N 行、熔断器状态、各主线程队列深度、最近一次失败原因、pending adjust 数量 —— 这些信息分散在 6 个类里，排障时必须逐个猜。

**修复建议**：①`Execute` 改 `catch (Exception)` + 全栈；②失败按语义分级（用户输入错 = Warn，内部异常 = Error）；③新增 `ValleyAgent_diag`：一屏打印健康面板（运行时模式 / TS 进程 PID+存活 / WS 状态+上次断连原因 / 熔断器 / 队列深度 / 最近 5 条错误摘要（从 `ModErrorLog` 读回） / 日志文件路径），并把同样的内容写进 `ValleyAgent-error.log` 便于远程排障。

---

## 5. P2：一致性与长期收敛

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| 1 | `core/llm-provider.ts:222-256` | 401/403（key 错/无权限）不在快速失败名单里（只有 402/429），会白重试 3 次（~7s+ 退避）后报 "LLM unavailable"；`describeError` 只取 `err.message`，ai-sdk 的 `statusCode`/`responseBody`/`cause`/`isRetryable` 都不入日志 —— **provider 返回的真实原因（"invalid api key"/"insufficient balance"/"model not found"）看不到** | 401/403/404 → 立即抛 `LLMConfigError`（新增档，`fallbackReason="config"`）；日志附 `statusCode` + `responseBody` 前 300 字符；超时（AbortError）单独分类为 `timeout` 并打出配置的 ms |
| 2 | `core/llm-provider.ts:270-283` `convertMessages` | `JSON.parse(tc.args)` 在消息转换里裸调；`LlmToolCall.args` 是 string 而 `AgentToolCall.args` 是 object（`core/types.ts:11-27`），两个类型混用时这里会抛 —— 而它会被 `withRetry` 包装成"LLM unavailable after 3 retries"，**把代码 bug 说成 LLM 故障**（与 §4.5 同一类误诊） | 转换处 try/catch + 明确报"内部消息格式错误"，不进重试链 |
| 3 | `core/tool-registry.ts:50-66` | 工具抛异常 → 只回 `Tool execution failed: {message}` 给 LLM，**不落任何日志**、不带工具名/参数/栈；运维只在 `ConsoleLogSubscriber` 的 `ok=false result=...` 里看到结果字符串 | `catch` 里 `console.error("[tool] {name} threw", err)`（配合 §3.6 修复才有价值），返回内容里带工具名 |
| 4 | `core/agent-loop.ts:170-207` `processToolCall` | 只有 `tools.execute` 被 try 包住；`beforeToolCall`/`afterToolCall` 钩子抛出会冒到外层 catch → **整轮 run 直接终止**（而不是把该工具标记失败继续） | 钩子调用各自 try/catch，失败按 `isError` 结果回喂 LLM |
| 5 | `stardew/log-tee.ts` | 只 patch `log/info/warn/error`，不含 `debug`/`trace`；`uncaughtException` 后进程继续运行且无节流（同一异常高频重复会刷爆文件，5MB 轮转下会把真正的历史现场挤掉） | 补 `debug`；全局处理器加"同签名 5s 去重"+ 计数 |
| 6 | `stardew/cli.ts:47-70` | `--port` 用 `parseInt` 不校验 NaN；未知参数静默忽略（`--llm-api-keyy` 拼错 → 报 "API key required"，误导方向） | 端口 NaN/越界直接 fatal；未知参数打 warn 并列出相近参数 |
| 7 | `Infrastructure/MainThreadWatchdog.cs:128-138` | `WatchLoop` 是 `new Thread` 上的 `while(true)`，**循环体无 try/catch**（内部 `CaptureDump` 有守卫，但 `PollStuckOperations`/`CheckMainThreadStall` 外层没有）。后台线程未处理异常在 .NET Core 会**终止进程** —— 取证仪器自己有把游戏带崩的理论路径 | 循环体包 `catch (Exception)` + 落盘 + `continue` |
| 8 | `Initialization/EventHandlerInitializer.cs:1788+` | `OnUpdateTicked` 里 `_ = Task.Run(async () => await MakeDecisionsAsync(...))` 4 处（1875/2085/2099/2112），全仓 16 处 fire-and-forget —— 依赖 `TaskScheduler.UnobservedTaskException` 才能落盘，而它**由 GC 触发**（时机不定/可能不触发） | 统一封 `SafeFireAndForget(task, "决策批")`：`ContinueWith` 里立即记 Error + `ModErrorLog`，并接 `StuckOperationTracker`（已有仪器，接线即可） |
| 9 | `Patches/*`、`Chat/*` | 静默 `return`（无日志）的早退分支较多，例如 `ChatBarRouter.ProcessPendingReplies` 的 `npc == null → continue`、`SubmitInput` 的 `_agentServerProvider == null && _dialogueTransport == null → return`（玩家敲了字，什么也不发生，日志无痕） | 玩家可感知的早退一律留一行 Debug（可节流），符合 `AGENTS.md §3.6` 铁律 |

---

## 6. 修复批次建议（按"止血 → 隔离 → 降级 → 提值 → 闭环"）

> 门槛沿用 `AGENTS.md §5`：TS 侧 `bun test` + `tsc --noEmit` + `check:protocol` 全绿；C# 侧编译 0 警告 + `dotnet test src/ValleyAgent.UnitTests` + 游戏内实测。每批结束跑 `node scripts/check-exception-hygiene.mjs --update-baseline` 把存量计数收紧（棘轮只许降不许升）。

| 批次 | 内容 | 为什么这个顺序 | 验证判据 |
|---|---|---|---|
| **PR1 可观测性止血**（纯 TS，零行为改动，风险最低） | §3.6 log-tee 的 Error 序列化 + §4.4 16 处级别提升 + §3.5 的 error 帧带 requestId/code + `messages.json` 补 `error` schema + §3.7 default 分支日志 + 探针脚本进 CI | 先让错误**看得见**，后面所有修复才有反馈回路 | `bun scripts/test/error-observability-probe.ts` 输出中：落盘日志含 message+stack；error 帧含 requestId；未知类型有 WARN 行；`log-tee.test.ts` 新增断言通过 |
| **PR2 C# 故障隔离** | §3.1 `SafeRun` 分段包装 `OnUpdateTicked` + §3.2 8 处 Harmony 外层守卫（失败回落原版）+ §4.8 5 处泵改 `catch (Exception)` + `OnSaving` per-agent 隔离 + §3.3 三循环 catch-all + 周期探活 | 把"一个功能坏 = 全模坏"改成"一个功能坏 = 只有它坏" | 新增单测：注入抛异常的段/泵/agent，断言其余段仍执行、日志只 1 条（节流）、原版路径可用；`CS-PUMP-NO-GUARD`/`CS-HARMONY-NO-GUARD`/`CS-LOOP-CATCH-GAP` 计数归零 |
| **PR3 初始化降级** | §3.4 `ServiceInitializer` 分段 + `DegradedFeatures` 表 + 不 rethrow 改落降级模式 + `RegisterHarmonyPatches` 捕 `Exception`/拆段/失败 `UnpatchSelf` + 玩家可读提示 | 保证"装上了但某子系统坏了"的玩家仍能玩原版 | 单测：某服务构造抛异常 → 容器仍可用、降级表有记录、无异常逃出 `OnSaveLoadedInitialize`；实机：删掉 `npc_prompts.json` 后进存档，游戏可玩且 `ValleyAgent-error.log` 有明确原因 |
| **PR4 日志提值** | §4.1 139 处 `{ex.Message}` → `{ex}` + §4.2 13 处级别提升 + §4.3 12 处静默降级补留痕 + 数据损坏文件隔离（`.corrupt-<ts>`）+ §4.5 `fallbackReason` 扩档（含 `messages.json` 取值约定） | 让日志"正确 + 有价值" | `CS-LOG-NO-STACK` 与 `CS-SILENT-CATCH`（非白名单部分）归零；`protocol-roundtrip` / 契约测试覆盖新 `fallbackReason` 取值 |
| **PR5 闭环与健康面板** | §3.9 `AdjustExecutor.Execute` 外层守卫 + 异常必回执 + TS 对账重发上限 + §3.8 `.then` 补 `.catch` + dialogue 服务端 90s 超时 + §4.7 熔断器接主对话路径 + §4.9 `ValleyAgent_diag` | 把"无限重试/永久 BUSY/永不熔断"三个不死不活的稳态清掉 | 单测：注入 C# 抛异常 → TS 收到 `internalError` 回执、重发 ≤3 次后停并打 ERROR；`bun scripts/test/agent-wedge-repro.ts` 退出码 0（`awaitAll-RESOLVED` + `isIdle(): true`）；实机：LLM key 改错 → 熔断器打开且 `ValleyAgent_diag` 能看到原因 |

**批次 ↔ issue 映射**（遗留项已在 GitHub 立项，逐条含证据/方向/验收）：

| 批次 | 对应 issue |
|---|---|
| PR1 可观测性止血 | #21（tee 销毁 Error）、#22（error 帧契约）、#26 第 4 项（16 处级别提升）、#27 第 2 项（对账重发上限）、#28（门禁与探针进 CI） |
| PR2 C# 故障隔离 | #24（tick 分段 / Harmony / WS 循环 / 看门狗 / fire-and-forget） |
| PR3 初始化降级 | #25（rethrow → 降级模式、半套补丁、`NpcPromptLoader` 秒退） |
| PR4 日志提值 | #26（139 处 `{ex}` / 47 处 level / 12 处静默 catch / `fallbackReason` 扩档） |
| PR5 闭环与健康面板 | #23（`awaitAll` 卡死 → NPC 永久 BUSY）、#27（adjust 必回执 / 熔断器接主路径 / `ValleyAgent_diag`） |

阻塞关系：#24 #25 #27 的 C# 部分依赖 #18（C# 门禁未常态化）；#28 附带 4 条猜想（C1/C2/C4/C6）的实机终验清单。

> **另立 #30（本轮顺带发现，优先级最高）**：GitHub Actions 自 2026-09-13 15:48 起**全线起不了 job**（11 个 run 全 `jobs=0`，含 main；PR #20/#29 的 checks 面板为空）。也就是说 `AGENTS.md §5` 声明的门禁（`bun test` / `typecheck` / `check:protocol` / 隐私扫描 / C# 单测）**这两天一次都没跑过**，#18 / #19 / #28 三者共同的前提（CI 能跑）目前不成立。已实测排除账户计费原因（07:26 有一次 `jobs=4` 的绿 run，在 main 失败 14 分钟之后），本地静态检查也排除 YAML 非法/重复键/tab；真因需 Actions 页面顶部的红字确认（#30 附终验三步）。

---

## 7. 已验证事实 vs 猜想（遵守 `AGENTS.md §2` 第 10 条）

### 7.1 已验证事实（有代码或实测支撑）

| # | 事实 | 证据 |
|---|---|---|
| F1 | `JSON.stringify(new Error("boom"))` === `"{}"`；tee 落盘丢失全部错误详情 | node/bun 实测 + 探针 §8（`[error] ... : {}`） |
| F2 | TS 未知消息类型静默 ack，落盘日志零痕迹 | 探针 1 实测 |
| F3 | TS handler 抛错回的 `error` 帧不含 requestId，字段名为 `message` | 探针 2/3 实测 + `server.ts:186` |
| F4 | C# `CheckErrorResponse` 读的是 `error` 字段（与 F3 不一致）→ 恒 "Unknown error" | `WebSocketClient.cs:76-85` 代码事实 |
| F5 | `error` 消息类型不在 `server/protocol/messages.json` | 解析 JSON 全文检索无 `error` key |
| F6 | TS 代码缺陷（快照字段缺失）被回成 `fallbackReason:"llm_error"` | 探针 4 实测 |
| F7 | `OnUpdateTicked` 397 行 0 try；`ProcessAgent` 105 行 0 try；8 处 Harmony 入口无外层守卫；WS 三循环只捕 2 类异常 | 扫描器 + 逐处人工复核 |
| F8 | 139/159 个记日志的 catch 只写 `ex.Message`；47 处 `Monitor.Log` 未给 level | 扫描器统计 |
| F9 | SMAPI `IMonitor.Log` 默认 level = **Trace**，Trace 默认不进控制台 | SMAPI 源码 `src/SMAPI/IMonitor.cs` + 官方 wiki Logging 页 |
| F10 | SMAPI 对**事件 handler** 异常有兜底：记 Error（带栈）后继续，不禁用 handler | SMAPI 源码 `Framework/Events/ManagedEvent.cs` `Raise()` |
| F11 | 熔断器 `RecordFailure` 只有 3 个调用点，均不在主对话路径 | 全仓 grep |
| F12 | `bun test` = 580 pass / 0 fail（审计基线，本次未改动任何生产代码） | 本环境实测 |
| F13 | `NpcPromptLoader` 构造无守卫；`getPhasePrompt` 缺失人设静默兜底 | 代码事实 |
| F14 | `Agent.prompt` 的 `stream.awaitAll().then(...)` 无 `.catch`，`activeRun` 仅在 then 分支清空 | 代码事实（`core/src/agent.ts:88-100`） |
| F15 | **订阅者抛异常 → proxy stream 永久挂起、`isIdle()` 恒 false、`errorMessage` 恒 null**（NPC 永久 BUSY 的机理成立） | `scripts/test/agent-wedge-repro.ts` 实测（原猜想 C5，已结案） |
| F16 | Bun 1.4.2 **确实**调用 `process.on("uncaughtException")` 与 `unhandledRejection`，且进程存活 | `bun -e` 实测：`HANDLER-RAN: boom` + `PROCESS-STILL-ALIVE`（原猜想 C3，已结案；副作用：进程带着未知状态继续跑，且原因落盘为 `{}`） |

### 7.2 猜想（未实测，需终验；不作为修复结论的依据）

| # | 猜想 | 推理链 | 反证条件 | 终验方案（判据） |
|---|---|---|---|---|
| **C1** | Harmony 补丁抛出会导致崩溃到桌面（而不只是该帧/该次交互失败） | Harmony 不捕补丁异常；`DialogueBox.draw` 在游戏渲染循环内；SMAPI 的兜底只覆盖 SMAPI 事件（F10） | 若 SMAPI 在 `SGame.Draw`/`UpdateGameInput` 外层有 try/catch 并只显示红字，则只降级不崩 | TestMod 加一个必抛异常的 `DrawPostfix`，进游戏开对话框：判据 = 进程是否存活 + SMAPI 日志归因文本 + `ValleyAgent-error.log` 是否有 `Unhandled` 条目 |
| **C2** | §3.1 的失效链会造成 SMAPI 日志洪水（~60 条带栈 Error/秒） | `UpdateTicked` 频率 = 60Hz；`ManagedEvent` 不去重（F10） | 若 SMAPI 对同一 handler 的重复异常做去重/限流，则量级小得多 | 单测/实机让某段确定性抛出，跑 60s 数 SMAPI 日志行数与文件增量 |
| ~~**C3**~~ | ~~Bun 是否调用 `uncaughtException` 处理器~~ | **已结案 → 事实 F16**（实测：处理器执行且进程存活） | — | 已执行 |
| **C4** | `ConsoleWindow=true` 下 TS 进程秒退时玩家看不到任何窗口内容 | `cmd /c start ... /wait` 随子进程退出而关闭窗口；该模式 stdout 不重定向（`ServerProcessManager.cs:304-326`） | 若 `start /wait` 在子进程异常退出时保留窗口（`cmd /k` 语义），则可见 | 实机把 `npc_prompts.json` 改名后启动游戏：判据 = 是否看到窗口内容 / C# 日志是否只有 "exited prematurely with code 1" |
| ~~**C5**~~ | ~~§3.8 的"NPC 永久 BUSY"是否可达~~ | **已结案 → 事实 F15**（`agent-wedge-repro.ts` 实测挂起） | — | 已执行（脚本已入库，可当回归守卫） |
| **C6** | §3.9 的无限对账重发在实机上会持续消耗（而非被 C# 幂等缓存挡掉） | C# 幂等缓存只在 `Execute` 成功返回时写入（`AdjustExecutor.cs:86-97`）；抛异常路径不写缓存 | 若异常发生在 `Cache` 之后的发送阶段，则缓存已写、重发会命中并返回原回执（不无限） | 单测：让 `ExecuteCore` 抛异常，连续调 `Execute` 3 次，断言缓存是否写入 / TS 侧重发是否收敛 |

---

## 8. 附录 A：实测证据（`scripts/test/error-observability-probe.ts` 输出摘录）

```
=== 探针 1：未知消息类型（模拟 C# 发了 TS 不认识的 type / 协议漂移）===
   reply = {"type":"ack","requestId":"unknown"}     ← 落盘日志里完全没有这条消息的痕迹

=== 探针 2：payload 为 null（畸形帧）===
控制台：TypeError: null is not an object (evaluating 'm.type')
          at routeMessage (protocol-adapter.ts:735:13) at message (server.ts:174:42)
   reply = {"type":"error","message":"TypeError: null is not an object (evaluating 'm.type')"}
                                                      ← 无 requestId：C# 只能等满 120s 超时

=== 探针 3：reconnect_sync 缺 agents 字段（handler 内部抛 TypeError）===
   reply = {"type":"error","message":"TypeError: undefined is not an object (evaluating 'req.agents.length')"}

=== 探针 4：dialogue 缺 worldSnapshot（TS 代码缺陷 → 玩家侧降级）===
   reply = {"type":"dialogue_response","requestId":"req-4","npcName":"Haley","speech":"......",
            "actions":[{"tool":"emote","args":{"emote_id":"question"}}],"emotion":"Neutral",
            "fallback":true,"fallbackReason":"llm_error"}     ← 代码缺陷被标成 LLM 故障

---- ValleyAgent-server.log（发行包 ServerConsoleWindow=true 下唯一的持久化现场）----
[...] [log]   [server] websocket connected
[...] [error] [server] message handler error: {}          ← 探针 2 的原因被销毁
[...] [error] [server] message handler error: {}          ← 探针 3 的原因被销毁
[...] [log]   [13:47:39] [recv] dialogue npc=Haley player="你好"
[...] [error] [dialogue] LLM failed for Haley: {}         ← 探针 4 的原因被销毁
[...] [log]   [13:47:39] [send] dialogue npc=Haley FALLBACK   ← 降级事件是 [log] 级，grep error 抓不到
```

对照实验（`JSON.stringify` 语义，node/bun 一致）：
```js
const e = new Error("boom"); e.code = "X";
JSON.stringify(e)  // => '{"code":"X"}'   ← message/stack 不可枚举，全丢
String(e)          // => 'Error: boom'    ← 兜底分支永远走不到，因为上面返回的不是 undefined
```

---

## 9. 附录 B：扫描器规则与当前存量

`node scripts/check-exception-hygiene.mjs`（297 处；P0 39 / P1 211 / P2 47）

| 规则 | 档 | 存量 | 含义 |
|---|---|---|---|
| `CS-HARMONY-NO-GUARD` | P0 | 8 | Harmony 补丁入口首条语句不是 try（§3.2） |
| `CS-PUMP-NO-GUARD` | P0 | 13 | 队列泵/串行调度方法体零 try（§3.1；含 `OnUpdateTicked` 397 行、`ProcessAgent` 105 行、两个 `ProcessPendingReplies`、`GoalExecutor.TickExecuting/TickReporting`、`OnPlayerWarped` 104 行等） |
| `CS-LOOP-CATCH-GAP` | P0 | 4 | 长跑循环未捕 `Exception`（§3.3 三处 + `MainThreadWatchdog.WatchLoop`） |
| `TS-ERR-OBJECT-TO-TEE` | P0 | 13 | `console.*` 以裸标识符传 Error 对象 → 落盘 `{}`（§3.6） |
| `TS-UNKNOWN-TYPE-SILENT` | P0 | 1 | 消息类型 switch 的 default 无日志（§3.7） |
| `CS-PUMP-NARROW-CATCH` | P1 | 5 | 泵内只捕窄类型（§4.8） |
| `CS-LOG-NO-STACK` | P1 | 139 | catch 日志只写 `ex.Message`（§4.1） |
| `CS-SILENT-CATCH` | P1 | 50 | catch 不记日志不重抛（§4.3，含约 22 处可接受） |
| `TS-FAIL-AT-LOG-LEVEL` | P1 | 16 | 失败/丢弃事件打在 `console.log`（§4.4） |
| `TS-THEN-NO-CATCH` | P1 | 1 | `.then()` 无 `.catch()`（§3.8） |
| `CS-LOG-DEFAULT-LEVEL` | P2 | 47 | `Monitor.Log` 未给 level → Trace（§4.2） |
| `TS-EMPTY-CATCH` | P2 | 0 | 空 catch 无注释（当前干净） |

门禁语义：基线 `scripts/exception-hygiene-baseline.json` 记录每个 `规则|文件` 的存量计数；**计数上升即 CI 失败**，下降则提示 `--update-baseline` 收紧。已实测：向 `emotion-engine.ts` 注入一处 `console.error("...", err)` → 合计 298、P0 40、退出码 1、报出精确的 `规则|文件: 基线 0 → 当前 1`。

建议接进 `.github/workflows/ci.yml` 的 `ts` job（纯 node，无需 bun/dotnet，秒级）：
```yaml
      - name: Exception hygiene gate
        run: node scripts/check-exception-hygiene.mjs
```

---

## 10. 一页纸回答用户的两个问题

1. **"默认假设每一个功能都是有错误的，这种情况下模组还能否正常运行？"**
   —— **TS 服务器侧：能**（顶层 try/catch + 人设三档兜底 + 工具错误回喂 + 原子落盘 + 幂等对账，`fault-injection.test.ts` 19 例覆盖）。
   —— **C# 模组侧：不能**。有 4 个"一处坏 = 一大片坏"的单点：tick 流水线（§3.1）、Harmony 补丁（§3.2）、WS 长跑循环（§3.3）、初始化链（§3.4）；外加 2 个"不死不活的稳态"：execute_adjust 无回执导致无限对账（§3.9）、`.then` 无 `.catch` 导致 NPC 永久 BUSY（§3.8）。
   —— 好消息是：修复不需要重构，**仓库里已经有正确范式**（`MultiplayerEventRouter` / `HostRequestHandlers` / `SocialPagePatch` / `AdjustExecutor` 的四阶段），把它一致化即可，PR2+PR3 两批就能把"能否正常运行"翻成 🟢。

2. **"能否在 log 或 CLI 中突出正确的、有价值的错误信息？"**
   —— **当前不能，且有一处是"销毁"级别**：发行包唯一持久化的 `ValleyAgent-server.log` 里，所有 `console.error(..., err)` 都落成 `{}`（§3.6，实测）。
   —— 其次是"价值折损"：87% 的 C# catch 丢栈（§4.1）、13 处关键丢弃/错误信号落在默认不可见的 Trace（§4.2）、16 处 TS 失败事件与正常日志同级（§4.4）、错误回包两端字段名对不上且不带 requestId（§3.5）、代码缺陷被标成 LLM 故障（§4.5）。
   —— **"突出"也缺一个抓手**：CLI 侧没有健康诊断命令（§4.9）。PR1（纯 TS，零行为改动）+ PR4 就能把这一项翻成 🟢；建议同时把 `ValleyAgent_diag` 做出来，让"哪里坏了"一屏可见。
